using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class JenkinsBuildServerAdapter : IBuildServerAdapter
    {
        public string Name => "Jenkins";

        private BuildServerIntegration _config;
        private string _repositoryPath;
        private HttpClient _httpClient;
        private static readonly object s_credentialLock = new();
        private static readonly SemaphoreSlim s_promptSemaphore = new(1, 1);
        private static readonly Dictionary<string, BuildServerCredentials> s_credentialCache = new();

        // Jenkins tree query for build info - includes displayName for build configuration info
        private const string JenkinsTreeBuildInfo = "number,displayName,result,timestamp,url,building,duration,actions[lastBuiltRevision[SHA1,branch[name]],totalCount,failCount,skipCount,parameters[name,value]]";

        public void Initialize(BuildServerIntegration config, string repositoryPath)
        {
            _config = config;
            _repositoryPath = repositoryPath;

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromMinutes(2);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SourceGit");

            // Base address
            if (!string.IsNullOrEmpty(config.ServerUrl))
            {
                var uri = new Uri(config.ServerUrl);
                _httpClient.BaseAddress = uri;
            }

            // Credentials via credential store
            if (Utils.CredentialStore.TryGet(_repositoryPath, config.Type, config.ServerUrl, out var user, out var secret))
            {
                if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(secret))
                {
                    var raw = $"{user}:{secret}";
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
                }
            }
        }

        public async Task<Dictionary<string, List<CommitBuildInfo>>> QueryBuildStatusAsync(List<string> commitShas)
        {
            var result = new Dictionary<string, List<CommitBuildInfo>>();

            if (_config is null || string.IsNullOrEmpty(_config.ServerUrl) || string.IsNullOrEmpty(_config.ProjectName))
                return result;

            try
            {
                // Support multiple projects separated by '|' like GitExtensions
                var projectNames = _config.ProjectName.Split(['|'], StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var projectName in projectNames)
                {
                    await QueryProjectBuildsAsync(projectName.Trim(), commitShas, result, hasRetriedWithCredentials: false);
                }

                return result;
            }
            catch (Exception ex)
            {
                // Log error but don't fail - build status is not critical
                Notification.Send(string.Empty, $"Failed to query Jenkins build status: {ex.Message}", true);
                return result;
            }
        }

        private async Task QueryProjectBuildsAsync(string projectName, List<string> commitShas, Dictionary<string, List<CommitBuildInfo>> result, bool hasRetriedWithCredentials)
        {
            var baseUrl = _config.ServerUrl.TrimEnd('/');
            string requestUrl;
            string realProjectName = projectName;

            // Handle GitExtensions-style "?m" suffix for multibranch pipelines
            if (projectName.EndsWith("?m"))
            {
                realProjectName = projectName.Substring(0, projectName.Length - 2);
                var jobUrl = $"{baseUrl}/job/{Uri.EscapeDataString(realProjectName)}/api/json";
                requestUrl = $"{jobUrl}?depth=2&tree=jobs[builds[{JenkinsTreeBuildInfo}]]";
            }
            else
            {
                var jobUrl = $"{baseUrl}/job/{Uri.EscapeDataString(projectName)}/api/json";
                requestUrl = $"{jobUrl}?depth=1&tree=builds[{JenkinsTreeBuildInfo}]";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden && !hasRetriedWithCredentials)
                {
                    var credentials = await GetBuildServerCredentialsAsync(useStoredCredentials: false);

                    if (credentials != null && !string.IsNullOrEmpty(credentials.Username))
                    {
                        // Store credentials in Windows Credential Manager
                        Utils.CredentialStore.Set(_repositoryPath, _config.Type, _config.ServerUrl, credentials.Username, credentials.Password);

                        // Update HTTP client with new credentials
                        var raw = $"{credentials.Username}:{credentials.Password}";
                        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);

                        // Retry the request with new credentials (only once)
                        await QueryProjectBuildsAsync(projectName, commitShas, result, hasRetriedWithCredentials: true);
                        return;
                    }

                    Notification.Send(string.Empty, $"Jenkins returned {response.StatusCode} for project '{projectName}'. Authentication required.", true);
                    return;
                }
                
                Notification.Send(string.Empty, $"Jenkins returned {response.StatusCode} for project '{projectName}'", true);
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            // Process freestyle job builds
            if (doc.RootElement.TryGetProperty("builds", out var buildsElement) && buildsElement.ValueKind == JsonValueKind.Array)
            {
                ProcessBuilds(buildsElement, baseUrl, realProjectName, commitShas, result);
            }

            // Process multi-branch pipeline jobs
            if (doc.RootElement.TryGetProperty("jobs", out var jobsElement) && jobsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var job in jobsElement.EnumerateArray())
                {
                    if (job.TryGetProperty("builds", out var jobBuilds) && jobBuilds.ValueKind == JsonValueKind.Array)
                    {
                        ProcessBuilds(jobBuilds, baseUrl, realProjectName, commitShas, result);
                    }
                }
            }
        }

        private void ProcessBuilds(JsonElement buildsElement, string baseUrl, string projectName, List<string> commitShas, Dictionary<string, List<CommitBuildInfo>> result)
        {
            foreach (var build in buildsElement.EnumerateArray())
            {
                var buildNumber = build.TryGetProperty("number", out var numberElement) ? numberElement.GetInt32() : 0;
                var buildUrl = build.TryGetProperty("url", out var urlElement) 
                    ? urlElement.GetString() 
                    : $"{baseUrl}/job/{Uri.EscapeDataString(projectName)}/{buildNumber}";

                // Get display name which often contains platform/config info (e.g., "1665dcfdf Windows x64 Release")
                var displayName = build.TryGetProperty("displayName", out var displayNameElement) 
                    ? displayNameElement.GetString() 
                    : null;

                // Check if build is still running
                var isBuilding = build.TryGetProperty("building", out var buildingElement) && buildingElement.GetBoolean();
                
                BuildStatus buildStatus;
                string resultStr;
                
                if (isBuilding)
                {
                    buildStatus = BuildStatus.InProgress;
                    resultStr = "BUILDING";
                }
                else if (build.TryGetProperty("result", out var resultElement) && resultElement.ValueKind != JsonValueKind.Null)
                {
                    resultStr = resultElement.GetString() ?? "UNKNOWN";
                    buildStatus = MapJenkinsStatus(resultStr);
                }
                else
                {
                    // No result and not building - might be queued or unknown state
                    buildStatus = BuildStatus.Unknown;
                    resultStr = "UNKNOWN";
                }

                // Build description: prefer displayName if it has useful info, otherwise use project + build number
                string description;
                if (!string.IsNullOrEmpty(displayName) && displayName != $"#{buildNumber}")
                {
                    // displayName often contains commit SHA + platform info like "1665dcfdf Windows x64 Release"
                    // Just use the displayName with result, no need to repeat the build number
                    description = $"{displayName}: {resultStr}";
                }
                else
                {
                    description = $"{projectName} #{buildNumber}: {resultStr}";
                }

                // Try to extract commit SHA from build actions
                var sha = ExtractCommitShaFromBuild(build, commitShas);
                if (!string.IsNullOrEmpty(sha))
                {
                    var buildInfo = new CommitBuildInfo
                    {
                        Status = buildStatus,
                        Description = description,
                        Url = buildUrl,
                        JobName = $"{projectName}#{buildNumber}", // Unique identifier for each build
                    };

                    // Add to the list for this SHA - collect ALL builds, not just one per job
                    if (!result.TryGetValue(sha, out var buildInfoList))
                    {
                        buildInfoList = new List<CommitBuildInfo>();
                        result[sha] = buildInfoList;
                    }

                    // Check if we already have this exact build (same build number)
                    var existingBuildIndex = buildInfoList.FindIndex(b => b.JobName == buildInfo.JobName);
                    if (existingBuildIndex >= 0)
                    {
                        // Update if the new status is "better" (completed > in-progress > unknown)
                        if (IsBetterStatus(buildStatus, buildInfoList[existingBuildIndex].Status))
                        {
                            buildInfoList[existingBuildIndex] = buildInfo;
                        }
                    }
                    else
                    {
                        // New build for this commit - add it to the list
                        buildInfoList.Add(buildInfo);
                    }
                }
            }

            // Sort builds for each commit by build number (descending - newest first)
            foreach (var kvp in result)
            {
                kvp.Value.Sort((a, b) =>
                {
                    // Extract build numbers from JobName (format: "projectName#buildNumber")
                    var aNum = ExtractBuildNumber(a.JobName);
                    var bNum = ExtractBuildNumber(b.JobName);
                    return bNum.CompareTo(aNum); // Descending order
                });
            }
        }

        private static int ExtractBuildNumber(string jobName)
        {
            var hashIndex = jobName.LastIndexOf('#');
            if (hashIndex >= 0 && hashIndex < jobName.Length - 1)
            {
                if (int.TryParse(jobName.Substring(hashIndex + 1), out var num))
                    return num;
            }
            return 0;
        }

        private static bool IsBetterStatus(BuildStatus newStatus, BuildStatus existingStatus)
        {
            // Priority: Completed (Success/Failure/Unstable) > InProgress > Stopped > Unknown
            static int StatusPriority(BuildStatus status) => status switch
            {
                BuildStatus.Success => 5,
                BuildStatus.Failure => 5,
                BuildStatus.Unstable => 5,
                BuildStatus.InProgress => 3,
                BuildStatus.Stopped => 2,
                BuildStatus.Unknown => 1,
                _ => 0,
            };

            return StatusPriority(newStatus) > StatusPriority(existingStatus);
        }

        private static BuildStatus MapJenkinsStatus(string jenkinsResult)
        {
            return jenkinsResult switch
            {
                "SUCCESS" => BuildStatus.Success,
                "FAILURE" => BuildStatus.Failure,
                "UNSTABLE" => BuildStatus.Unstable,
                "ABORTED" => BuildStatus.Stopped,
                null => BuildStatus.InProgress,
                "" => BuildStatus.InProgress,
                _ => BuildStatus.Unknown,
            };
        }

        private static string ExtractCommitShaFromBuild(JsonElement build, List<string> commitShas)
        {
            if (!build.TryGetProperty("actions", out var actionsElement) || actionsElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var action in actionsElement.EnumerateArray())
            {
                // Check lastBuiltRevision (set by Git plugin) - this is the most reliable source
                if (action.TryGetProperty("lastBuiltRevision", out var revElement))
                {
                    if (revElement.TryGetProperty("SHA1", out var sha1Element))
                    {
                        var sha = sha1Element.GetString();
                        if (!string.IsNullOrEmpty(sha))
                        {
                            var matchedSha = FindMatchingSha(sha, commitShas);
                            if (matchedSha != null)
                                return matchedSha;
                        }
                    }
                }

                // Look for GIT_COMMIT in parameters (for parameterized builds)
                if (action.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var param in parametersElement.EnumerateArray())
                    {
                        if (param.TryGetProperty("name", out var nameElement))
                        {
                            var paramName = nameElement.GetString();
                            if (paramName == "GIT_COMMIT" || paramName == "ghprbActualCommit" || paramName == "sha1")
                            {
                                if (param.TryGetProperty("value", out var valueElement))
                                {
                                    var sha = valueElement.GetString();
                                    if (!string.IsNullOrEmpty(sha))
                                    {
                                        var matchedSha = FindMatchingSha(sha, commitShas);
                                        if (matchedSha != null)
                                            return matchedSha;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        private static string FindMatchingSha(string buildSha, List<string> commitShas)
        {
            // Normalize the SHA for comparison (Jenkins might return full 40-char or abbreviated)
            var normalizedBuildSha = buildSha.ToLowerInvariant();
            
            foreach (var commitSha in commitShas)
            {
                var normalizedCommitSha = commitSha.ToLowerInvariant();
                
                // Check if either SHA is a prefix of the other
                if (normalizedBuildSha.StartsWith(normalizedCommitSha, StringComparison.Ordinal) ||
                    normalizedCommitSha.StartsWith(normalizedBuildSha, StringComparison.Ordinal))
                {
                    return commitSha; // Return the original commit SHA from the list
                }
            }

            return null;
        }

        private async Task<BuildServerCredentials> GetBuildServerCredentialsAsync(bool useStoredCredentials)
        {
            var credentialKey = $"{_repositoryPath}|{_config.Type}|{_config.ServerUrl}";

            // Check cache and credential store inside lock
            lock (s_credentialLock)
            {
                // Try to use cached credentials first
                if (useStoredCredentials && s_credentialCache.TryGetValue(credentialKey, out var cached))
                {
                    return cached;
                }

                // Try Windows Credential Manager
                if (useStoredCredentials && Utils.CredentialStore.TryGet(_repositoryPath, _config.Type, _config.ServerUrl, out var user, out var secret))
                {
                    var storedCreds = new BuildServerCredentials
                    {
                        Username = user,
                        Password = secret,
                        ServerName = _config.ServerUrl
                    };
                    s_credentialCache[credentialKey] = storedCreds;
                    return storedCreds;
                }
            }

            // Serialized credential prompt to avoid multiple dialogs
            await s_promptSemaphore.WaitAsync();
            try
            {
                // Check cache again in case another thread just filled it
                lock (s_credentialLock)
                {
                    if (s_credentialCache.TryGetValue(credentialKey, out var cached))
                    {
                        return cached;
                    }
                }

                // Prompt user for credentials on UI thread (outside of lock)
                var credentials = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                    App.AskBuildServerCredentialsAsync(_config.ServerUrl));

                if (credentials != null && !string.IsNullOrEmpty(credentials.Username))
                {
                    lock (s_credentialLock)
                    {
                        s_credentialCache[credentialKey] = credentials;
                    }
                    return credentials;
                }

                return null;
            }
            finally
            {
                s_promptSemaphore.Release();
            }
        }
    }
}

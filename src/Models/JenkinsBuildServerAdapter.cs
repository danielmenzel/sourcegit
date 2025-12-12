using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public class JenkinsBuildServerAdapter : IBuildServerAdapter
    {
        public string Name => "Jenkins";

        private BuildServerIntegration _config;
        private HttpClient _httpClient;

        public void Initialize(BuildServerIntegration config, string repositoryPath)
        {
            _config = config;

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SourceGit");

            // Base address
            if (!string.IsNullOrEmpty(config.ServerUrl))
            {
                var uri = new Uri(config.ServerUrl);
                _httpClient.BaseAddress = uri;
            }

            // Credentials via DPAPI store (Windows-only)
            if (Utils.CredentialStore.TryGet(repositoryPath, config.Type, config.ServerUrl, out var user, out var secret))
            {
                if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(secret))
                {
                    var raw = $"{user}:{secret}";
                    var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
                }
            }
        }

        public async Task<Dictionary<string, CommitBuildInfo>> QueryBuildStatusAsync(List<string> commitShas)
        {
            var result = new Dictionary<string, CommitBuildInfo>();

            if (_config is null || string.IsNullOrEmpty(_config.ServerUrl) || string.IsNullOrEmpty(_config.ProjectName))
                return result;

            try
            {
                // Jenkins typically uses "blue ocean" REST API
                // Format: {serverUrl}/job/{projectName}/api/json?tree=builds[number,result,startTime,estimatedDuration]
                var jobUrl = $"{_config.ServerUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(_config.ProjectName)}/api/json";
                var requestUrl = $"{jobUrl}?tree=builds[number,result,startTime,estimatedDuration,actions[*[*]]]&depth=2";

                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                using var response = await _httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                    return result;

                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("builds", out var buildsElement) && buildsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var build in buildsElement.EnumerateArray())
                    {
                        if (!build.TryGetProperty("result", out var resultElement))
                            continue;

                        var resultStr = resultElement.GetString() ?? "UNKNOWN";
                        var buildNumber = build.TryGetProperty("number", out var numberElement) ? numberElement.GetInt32() : 0;
                        var buildUrl = $"{_config.ServerUrl.TrimEnd('/')}/job/{Uri.EscapeDataString(_config.ProjectName)}/{buildNumber}";

                        var buildStatus = MapJenkinsStatus(resultStr);
                        var description = $"Jenkins Build #{buildNumber}: {resultStr}";

                        // Try to extract commit SHA from build parameters or actions
                        var sha = ExtractCommitShaFromBuild(build, commitShas);
                        if (!string.IsNullOrEmpty(sha) && !result.ContainsKey(sha))
                        {
                            result[sha] = new CommitBuildInfo
                            {
                                Status = buildStatus,
                                Description = description,
                                Url = buildUrl,
                            };
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                // Log error but don't fail - build status is not critical
                App.RaiseException(string.Empty, $"Failed to query Jenkins build status: {ex.Message}");
                return result;
            }
        }

        private BuildStatus MapJenkinsStatus(string jenkinsResult)
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

        private string ExtractCommitShaFromBuild(JsonElement build, List<string> commitShas)
        {
            // Try to extract from build parameters
            if (build.TryGetProperty("actions", out var actionsElement) && actionsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var action in actionsElement.EnumerateArray())
                {
                    // Look for GIT_COMMIT parameter
                    if (action.TryGetProperty("parameters", out var parametersElement) && parametersElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var param in parametersElement.EnumerateArray())
                        {
                            if (param.TryGetProperty("name", out var nameElement) && nameElement.GetString() == "GIT_COMMIT")
                            {
                                if (param.TryGetProperty("value", out var valueElement))
                                {
                                    var sha = valueElement.GetString();
                                    if (!string.IsNullOrEmpty(sha) && commitShas.Any(x => x.StartsWith(sha, StringComparison.OrdinalIgnoreCase) || sha.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                                        return sha;
                                }
                            }
                        }
                    }

                    // Also check lastBuiltRevision
                    if (action.TryGetProperty("lastBuiltRevision", out var revElement))
                    {
                        if (revElement.TryGetProperty("SHA1", out var sha1Element))
                        {
                            var sha = sha1Element.GetString();
                            if (!string.IsNullOrEmpty(sha) && commitShas.Any(x => x.StartsWith(sha, StringComparison.OrdinalIgnoreCase) || sha.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
                                return sha;
                        }
                    }
                }
            }

            return null;
        }
    }
}

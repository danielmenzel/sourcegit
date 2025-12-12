using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class BuildServerPoller : IDisposable
    {
        private readonly string _repositoryPath;
        private readonly Func<Models.IBuildServerAdapter> _adapterFactory;
        private readonly Action<string, Models.CommitBuildInfo> _onStatusChanged;
        private Timer _pollingTimer;
        private bool _isPolling;
        private DateTime _lastPollTime;
        private bool _hasRunningBuilds;

        private const int ShortPollInterval = 10000;  // 10 seconds
        private const int LongPollInterval = 120000;  // 120 seconds

        public BuildServerPoller(
            string repositoryPath,
            Func<Models.IBuildServerAdapter> adapterFactory,
            Action<string, Models.CommitBuildInfo> onStatusChanged)
        {
            _repositoryPath = repositoryPath;
            _adapterFactory = adapterFactory;
            _onStatusChanged = onStatusChanged;
        }

        public void StartPolling(List<Models.Commit> commits)
        {
            if (_isPolling)
                return;

            _isPolling = true;
            _lastPollTime = DateTime.MinValue;
            _hasRunningBuilds = true;

            // Start immediately and then schedule recurring polls
            Task.Run(() => PollBuildStatus(commits));
        }

        public void StopPolling()
        {
            _isPolling = false;
            _pollingTimer?.Dispose();
            _pollingTimer = null;
        }

        public void UpdateCommits(List<Models.Commit> commits)
        {
            if (!_isPolling)
                return;

            // Trigger an immediate poll with new commits
            Task.Run(() => PollBuildStatus(commits));
        }

        public void Dispose()
        {
            StopPolling();
        }

        private async Task PollBuildStatus(List<Models.Commit> commits)
        {
            if (!_isPolling || commits is null || commits.Count == 0)
                return;

            try
            {
                var adapter = _adapterFactory();
                if (adapter is null)
                    return;

                // Extract commit SHAs
                var commitShas = commits.Select(c => c.SHA).ToList();

                // Query build status
                var buildStatuses = await adapter.QueryBuildStatusAsync(commitShas);

                if (buildStatuses is null || buildStatuses.Count == 0)
                {
                    _hasRunningBuilds = false;
                }
                else
                {
                    // Update commits with build status
                    foreach (var commit in commits)
                    {
                        if (buildStatuses.TryGetValue(commit.SHA, out var buildInfo))
                        {
                            commit.BuildInfo = buildInfo;
                            _onStatusChanged?.Invoke(commit.SHA, buildInfo);

                            // Check if any builds are still running
                            if (buildInfo.Status == Models.BuildStatus.InProgress)
                                _hasRunningBuilds = true;
                        }
                    }
                }

                _lastPollTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                App.RaiseException(string.Empty, $"Failed to poll build status: {ex.Message}");
            }
            finally
            {
                // Schedule next poll based on whether there are running builds
                ScheduleNextPoll(commits);
            }
        }

        private void ScheduleNextPoll(List<Models.Commit> commits)
        {
            if (!_isPolling)
                return;

            // Use short interval if there are running builds, otherwise use long interval
            var interval = _hasRunningBuilds ? ShortPollInterval : LongPollInterval;

            _pollingTimer?.Dispose();
            _pollingTimer = new Timer(
                async _ => await PollBuildStatus(commits),
                null,
                interval,
                Timeout.Infinite);
        }
    }
}

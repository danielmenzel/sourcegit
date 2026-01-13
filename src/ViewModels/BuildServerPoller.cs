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
        private readonly Action _onStatusChanged;
        private Timer _pollingTimer;
        private volatile bool _isPolling;
        private volatile bool _isPollInProgress;
        private DateTime _lastPollTime;
        private bool _hasRunningBuilds;
        private List<Models.Commit> _currentCommits;
        private readonly object _pollLock = new();

        private const int ShortPollInterval = 10000;  // 10 seconds
        private const int LongPollInterval = 120000;  // 120 seconds

        public BuildServerPoller(
            string repositoryPath,
            Func<Models.IBuildServerAdapter> adapterFactory,
            Action onStatusChanged)
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
            _currentCommits = commits;

            // Start immediately and then schedule recurring polls
            Task.Run(PollBuildStatusAsync);
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

            _currentCommits = commits;
            // Don't trigger immediate poll - let the timer handle it
        }

        public void Dispose()
        {
            StopPolling();
        }

        private async Task PollBuildStatusAsync()
        {
            // Prevent concurrent polling
            lock (_pollLock)
            {
                if (_isPollInProgress || !_isPolling)
                    return;
                _isPollInProgress = true;
            }

            try
            {
                var commits = _currentCommits;
                if (commits is null || commits.Count == 0)
                    return;

                var adapter = _adapterFactory();
                if (adapter is null)
                    return;

                // Limit to first 100 commits to avoid overwhelming Jenkins
                var commitShas = commits.Take(100).Select(c => c.SHA).ToList();

                // Query build status
                var buildStatuses = await adapter.QueryBuildStatusAsync(commitShas);

                if (!_isPolling)
                    return;

                _hasRunningBuilds = false;
                var hasUpdates = false;

                if (buildStatuses != null && buildStatuses.Count > 0)
                {
                    // Update commits with build status
                    foreach (var commit in commits)
                    {
                        if (buildStatuses.TryGetValue(commit.SHA, out var buildInfos))
                        {
                            commit.BuildInfos = buildInfos;
                            hasUpdates = true;

                            // Check if any builds are still running
                            foreach (var buildInfo in buildInfos)
                            {
                                if (buildInfo.Status == Models.BuildStatus.InProgress)
                                {
                                    _hasRunningBuilds = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Notify once after all updates are complete
                if (hasUpdates)
                    _onStatusChanged?.Invoke();

                _lastPollTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BuildServerPoller] Poll failed: {ex.Message}");
            }
            finally
            {
                lock (_pollLock)
                {
                    _isPollInProgress = false;
                }

                // Schedule next poll based on whether there are running builds
                ScheduleNextPoll();
            }
        }

        private void ScheduleNextPoll()
        {
            if (!_isPolling)
                return;

            // Use short interval if there are running builds, otherwise use long interval
            var interval = _hasRunningBuilds ? ShortPollInterval : LongPollInterval;

            _pollingTimer?.Dispose();
            _pollingTimer = new Timer(
                _ => Task.Run(PollBuildStatusAsync),
                null,
                interval,
                Timeout.Infinite);
        }
    }
}

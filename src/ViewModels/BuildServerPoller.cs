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
        private readonly object _pollLock = new();
        private Models.IBuildServerAdapter _adapter;
        private CancellationTokenSource _cancellation;
        private Task _pollingTask = Task.CompletedTask;
        private List<Models.Commit> _currentCommits = [];
        private bool _disposed;

        private const int ShortPollInterval = 30000;
        private const int LongPollInterval = 120000;

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
            ArgumentNullException.ThrowIfNull(commits);
            lock (_pollLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _currentCommits = new List<Models.Commit>(commits);
                if (_cancellation != null)
                    return;

                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                var previous = _pollingTask;
                // A restart must wait for the previous request to stop. The old
                // loop cannot publish results or schedule another request.
                _pollingTask = Task.Run(() => PollBuildStatusAsync(previous, cancellation));
            }
        }

        public void StopPolling()
        {
            lock (_pollLock)
            {
                _cancellation?.Cancel();
                _cancellation = null;
            }
        }

        public void Dispose()
        {
            Models.IBuildServerAdapter adapter = null;
            lock (_pollLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _cancellation?.Cancel();
                _cancellation = null;
                if (_pollingTask.IsCompleted)
                {
                    adapter = _adapter;
                    _adapter = null;
                }
                // An active loop disposes its adapter after the request exits.
            }
            adapter?.Dispose();
        }

        private async Task PollBuildStatusAsync(Task previous, CancellationTokenSource cancellation)
        {
            var token = cancellation.Token;
            try
            {
                await previous.ConfigureAwait(false);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var hasRunningBuilds = false;
                    try
                    {
                        hasRunningBuilds = await QueryBuildStatusAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Some transports report a closed connection instead of
                        // OperationCanceledException. A restart must still work.
                        token.ThrowIfCancellationRequested();
                        System.Diagnostics.Debug.WriteLine($"[BuildServerPoller] {_repositoryPath}: {ex.Message}");
                    }

                    var interval = hasRunningBuilds ? ShortPollInterval : LongPollInterval;
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Closing a repository or stopping polling cancels the body read too.
            }
            finally
            {
                Models.IBuildServerAdapter adapter = null;
                lock (_pollLock)
                {
                    if (ReferenceEquals(_cancellation, cancellation))
                        _cancellation = null;
                    if (_disposed)
                    {
                        adapter = _adapter;
                        _adapter = null;
                    }
                }
                cancellation.Dispose();
                adapter?.Dispose();
            }
        }

        private async Task<bool> QueryBuildStatusAsync(CancellationToken cancellationToken)
        {
            List<string> commitShas;
            lock (_pollLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                commitShas = _currentCommits.Take(100).Select(c => c.SHA).ToList();
            }
            if (commitShas.Count == 0)
                return false;

            _adapter ??= _adapterFactory();
            if (_adapter == null)
                return false;

            cancellationToken.ThrowIfCancellationRequested();
            var buildStatuses = await _adapter.QueryBuildStatusAsync(commitShas, cancellationToken).ConfigureAwait(false);
            List<Models.Commit> commits;
            lock (_pollLock)
            {
                // Ignore responses from cancelled requests. Each commit list is
                // replaced as a whole, so it can be read outside the lock.
                cancellationToken.ThrowIfCancellationRequested();
                commits = _currentCommits;
            }
            if (buildStatuses == null)
                return false;

            var hasRunningBuilds = false;
            var hasUpdates = false;
            foreach (var commit in commits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buildStatuses.TryGetValue(commit.SHA, out var buildInfos))
                {
                    // Property-change handlers can dispatch to the UI thread.
                    // Do not hold the polling lock while invoking them.
                    commit.BuildInfos = buildInfos;
                    hasUpdates = true;
                    hasRunningBuilds |= buildInfos.Any(b => b.Status == Models.BuildStatus.InProgress);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (hasUpdates)
                _onStatusChanged?.Invoke();
            return hasRunningBuilds;
        }
    }
}

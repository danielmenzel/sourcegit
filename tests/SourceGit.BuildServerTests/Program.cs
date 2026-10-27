using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using SourceGit.Models;
using SourceGit.ViewModels;

namespace SourceGit.BuildServerTests
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            var tests = new (string Name, Func<Task> Run)[]
            {
                (nameof(FreestyleQueriesHaveABuildLimit), FreestyleQueriesHaveABuildLimit),
                (nameof(MultibranchQueriesHaveABuildLimit), MultibranchQueriesHaveABuildLimit),
                (nameof(QueriesExcludeUnusedFields), QueriesExcludeUnusedFields),
                (nameof(BuildStatusStillMatchesCommits), BuildStatusStillMatchesCommits),
                (nameof(StopCancelsTheCurrentQuery), StopCancelsTheCurrentQuery),
                (nameof(DisposeReleasesTheAdapter), DisposeReleasesTheAdapter),
                (nameof(DisposedPollersCannotRestart), DisposedPollersCannotRestart),
                (nameof(CancellationStopsTheResponseBody), CancellationStopsTheResponseBody),
                (nameof(RestartRejectsLateResults), RestartRejectsLateResults),
                (nameof(CancelledTransportErrorDoesNotBreakRestart), CancelledTransportErrorDoesNotBreakRestart),
                (nameof(RepeatedStartUpdatesCommitsWithoutAnotherQuery), RepeatedStartUpdatesCommitsWithoutAnotherQuery),
                (nameof(EmptyHistoryCanResumePolling), EmptyHistoryCanResumePolling),
                (nameof(MultipleProjectsUseSeparateBoundedRequests), MultipleProjectsUseSeparateBoundedRequests),
                (nameof(StatusHandlersRunOutsideThePollingLock), StatusHandlersRunOutsideThePollingLock),
            };

            var failures = 0;
            foreach (var test in tests)
            {
                try
                {
                    await test.Run().WaitAsync(TimeSpan.FromSeconds(5));
                    Console.WriteLine($"PASS {test.Name}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
            return failures == 0 ? 0 : 1;
        }

        private static async Task FreestyleQueriesHaveABuildLimit()
        {
            var query = await CaptureQuery("sample");
            Check(query.Contains("builds[") && query.EndsWith("]{0,100}", StringComparison.Ordinal),
                $"Expected a limit of 100 builds in: {query}");
        }

        private static async Task MultibranchQueriesHaveABuildLimit()
        {
            var query = await CaptureQuery("sample?m");
            Check(query.Contains("jobs[builds[") && query.EndsWith("]{0,100}]", StringComparison.Ordinal),
                $"Expected a limit of 100 builds per branch in: {query}");
        }

        private static async Task QueriesExcludeUnusedFields()
        {
            var query = await CaptureQuery("sample?m");
            foreach (var field in new[] { "timestamp", "duration", "totalCount", "failCount", "skipCount", "branch[name]" })
                Check(!query.Contains(field), $"Unused field is still requested: {field}");
        }

        private static async Task BuildStatusStillMatchesCommits()
        {
            using var handler = new RecordingHandler("""
                {"builds":[
                  {"number":7,"displayName":"Windows x64","result":"SUCCESS","building":false,
                   "url":"https://example.invalid/job/sample/7/",
                   "actions":[{"lastBuiltRevision":{"SHA1":"abcdef0123456789"}}]},
                  {"number":8,"building":true,
                   "actions":[{"parameters":[{"name":"GIT_COMMIT","value":"abcdef0123456789"}]}]}
                ]}
                """);
            using var client = new HttpClient(handler);
            var adapter = CreateAdapter(client, "sample");
            var result = await adapter.QueryBuildStatusAsync(["abcdef0"]);
            Check(result["abcdef0"].Count == 2, "Both builds must remain visible.");
            Check(result["abcdef0"][0].Status == BuildStatus.InProgress, "The newest build must be first.");
            Check(result["abcdef0"][1].Status == BuildStatus.Success, "Completed status must be retained.");
        }

        private static async Task StopCancelsTheCurrentQuery()
        {
            var adapter = new BlockingAdapter();
            using var poller = new BuildServerPoller("test", () => adapter, () => { });
            poller.StartPolling([new Commit { SHA = "abcdef0" }]);
            await adapter.Entered.Task;
            try
            {
                poller.StopPolling();
                Check(adapter.Token.IsCancellationRequested, "Stopping the poller did not cancel the request.");
            }
            finally
            {
                adapter.Release.TrySetResult([]);
            }
        }

        private static async Task DisposeReleasesTheAdapter()
        {
            var adapter = new BlockingAdapter();
            using var poller = new BuildServerPoller("test", () => adapter, () => { });
            poller.StartPolling([new Commit { SHA = "abcdef0" }]);
            await adapter.Entered.Task;
            poller.Dispose();
            adapter.Release.TrySetResult([]);
            // Dispose can finish after the asynchronous query exits.
            await adapter.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }

        private static Task DisposedPollersCannotRestart()
        {
            var adapter = new BlockingAdapter();
            using var poller = new BuildServerPoller("test", () => adapter, () => { });
            poller.Dispose();
            try
            {
                poller.StartPolling([new Commit { SHA = "abcdef0" }]);
            }
            catch (ObjectDisposedException)
            {
                return Task.CompletedTask;
            }
            finally
            {
                poller.StopPolling();
                adapter.Release.TrySetResult([]);
            }
            throw new InvalidOperationException("A disposed poller accepted new work.");
        }

        private static async Task CancellationStopsTheResponseBody()
        {
            using var stream = new WaitingStream();
            using var handler = new StreamingHandler(stream);
            using var client = new HttpClient(handler);
            using var cancellation = new CancellationTokenSource();
            var adapter = CreateAdapter(client, "sample");
            var notifications = 0;
            void OnNotification(Notification _) => notifications++;
            Notification.Raised += OnNotification;
            try
            {
                var query = adapter.QueryBuildStatusAsync(["abcdef0"], cancellation.Token);
                await stream.ReadStarted.Task;
                cancellation.Cancel();
                try
                {
                    await query;
                    throw new InvalidOperationException("Cancellation did not stop the response body.");
                }
                catch (OperationCanceledException)
                {
                    Check(stream.IsDisposed, "The response stream was not disposed.");
                    Check(notifications == 0, "Cancellation must not produce an error notification.");
                }
            }
            finally
            {
                Notification.Raised -= OnNotification;
            }
        }

        private static async Task RestartRejectsLateResults()
        {
            var adapter = new QueuedAdapter();
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updates = 0;
            using var poller = new BuildServerPoller("test", () => adapter, () =>
            {
                Interlocked.Increment(ref updates);
                changed.TrySetResult();
            });
            var oldCommit = new Commit { SHA = "old" };
            var currentCommit = new Commit { SHA = "current" };
            poller.StartPolling([oldCommit]);
            var oldQuery = await adapter.Queries.Reader.ReadAsync();
            poller.StopPolling();
            poller.StartPolling([currentCommit]);
            Check(oldQuery.Token.IsCancellationRequested, "The previous request was not cancelled.");

            // Simulate a transport that returns data despite cancellation.
            oldQuery.Result.SetResult(StatusFor("old"));
            var currentQuery = await adapter.Queries.Reader.ReadAsync();
            Check(oldCommit.BuildInfos == null && updates == 0, "The old request published stale results.");
            Check(currentQuery.Commits.Count == 1 && currentQuery.Commits[0] == "current", "The restart used old commits.");
            Check(adapter.MaxActiveQueries == 1, "Two requests ran at the same time.");
            currentQuery.Result.SetResult(StatusFor("current"));
            await changed.Task;
            Check(currentCommit.BuildInfos.Count == 1 && updates == 1, "The new result was not published exactly once.");
            poller.Dispose();
            await PollingTask(poller);
            Check(adapter.DisposeCount == 1, "The adapter must be disposed exactly once.");
        }

        private static async Task CancelledTransportErrorDoesNotBreakRestart()
        {
            var adapter = new QueuedAdapter();
            using var poller = new BuildServerPoller("test", () => adapter, () => { });
            poller.StartPolling([new Commit { SHA = "old" }]);
            var oldQuery = await adapter.Queries.Reader.ReadAsync();
            poller.StopPolling();
            poller.StartPolling([new Commit { SHA = "new" }]);
            oldQuery.Result.SetException(new HttpRequestException("Connection closed during cancellation."));
            var newQuery = await adapter.Queries.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            poller.Dispose();
            newQuery.Result.SetResult([]);
            await PollingTask(poller);
            Check(adapter.DisposeCount == 1, "The adapter was not disposed after the restart.");
        }

        private static async Task RepeatedStartUpdatesCommitsWithoutAnotherQuery()
        {
            var adapter = new QueuedAdapter();
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var poller = new BuildServerPoller("test", () => adapter, () => changed.TrySetResult());
            var oldCommit = new Commit { SHA = "same" };
            var newCommit = new Commit { SHA = "same" };
            poller.StartPolling([oldCommit]);
            var query = await adapter.Queries.Reader.ReadAsync();
            poller.StartPolling([newCommit]);
            query.Result.SetResult(StatusFor("same"));
            await changed.Task;
            poller.StopPolling();
            await PollingTask(poller);
            Check(adapter.QueryCount == 1, "Refreshing commits started a duplicate request.");
            Check(oldCommit.BuildInfos == null && newCommit.BuildInfos.Count == 1, "The current commit list was not updated.");
            poller.Dispose();
            Check(adapter.DisposeCount == 1, "An idle poller did not dispose its adapter.");
        }

        private static async Task EmptyHistoryCanResumePolling()
        {
            var adapter = new QueuedAdapter();
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var poller = new BuildServerPoller("test", () => adapter, () => changed.TrySetResult());
            poller.StartPolling([]);
            poller.StopPolling();
            await PollingTask(poller);
            Check(adapter.QueryCount == 0, "An empty history caused a network request.");
            poller.StartPolling([new Commit { SHA = "resumed" }]);
            var query = await adapter.Queries.Reader.ReadAsync();
            query.Result.SetResult(StatusFor("resumed"));
            await changed.Task;
            poller.Dispose();
            await PollingTask(poller);
            Check(adapter.QueryCount == 1, "Polling did not resume exactly once.");
        }

        private static async Task MultipleProjectsUseSeparateBoundedRequests()
        {
            using var handler = new RecordingHandler("{}");
            using var client = new HttpClient(handler);
            using var adapter = CreateAdapter(client, " first job | second?m ");
            await adapter.QueryBuildStatusAsync(["abcdef0"]);
            Check(handler.Urls.Count == 2, "Expected one request per configured project.");
            Check(handler.Urls[0].AbsolutePath == "/job/first%20job/api/json", "The job name was not escaped.");
            foreach (var url in handler.Urls)
                Check(Uri.UnescapeDataString(url.Query).Contains("{0,100}"), "A project request has no build limit.");
        }

        private static async Task StatusHandlersRunOutsideThePollingLock()
        {
            var adapter = new QueuedAdapter();
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BuildServerPoller poller = null;
            poller = new BuildServerPoller("test", () => adapter, () =>
            {
                var stopped = Task.Run(poller.StopPolling);
                if (stopped.Wait(TimeSpan.FromSeconds(1)))
                    handled.TrySetResult();
                else
                    handled.TrySetException(new InvalidOperationException("The status handler holds the polling lock."));
            });
            using (poller)
            {
                poller.StartPolling([new Commit { SHA = "test" }]);
                var query = await adapter.Queries.Reader.ReadAsync();
                query.Result.SetResult(StatusFor("test"));
                await handled.Task;
                await PollingTask(poller);
            }
        }

        private static Dictionary<string, List<CommitBuildInfo>> StatusFor(string sha) =>
            new() { [sha] = [new CommitBuildInfo { Status = BuildStatus.Success }] };

        private static Task PollingTask(BuildServerPoller poller) =>
            (Task)typeof(BuildServerPoller).GetField("_pollingTask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(poller);

        private static async Task<string> CaptureQuery(string project)
        {
            using var handler = new RecordingHandler("{}");
            using var client = new HttpClient(handler);
            var adapter = CreateAdapter(client, project);
            await adapter.QueryBuildStatusAsync(["abcdef0"]);
            Check(handler.Url != null, "No request was sent.");
            return Uri.UnescapeDataString(handler.Url.Query);
        }

        private static JenkinsBuildServerAdapter CreateAdapter(HttpClient client, string project)
        {
            // Inject a fake transport without calling Initialize, which reads the
            // credential store. No request can reach a real server.
            var adapter = new JenkinsBuildServerAdapter();
            SetField(adapter, "_config", new BuildServerIntegration
            {
                ServerUrl = "https://example.invalid",
                ProjectName = project,
                EnableQueryBuildStatus = true,
            });
            SetField(adapter, "_httpClient", client);
            return adapter;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Check(field != null, $"Missing test injection field: {name}");
            field.SetValue(target, value);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private sealed class RecordingHandler(string json) : HttpMessageHandler
        {
            public Uri Url { get; private set; }
            public List<Uri> Urls { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Url = request.RequestUri;
                Urls.Add(Url);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
        }

        private sealed class StreamingHandler(Stream stream) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }

        private sealed class WaitingStream : Stream
        {
            public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsDisposed { get; private set; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ReadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            protected override void Dispose(bool disposing)
            {
                IsDisposed = true;
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
        }

        private sealed record PendingQuery(List<string> Commits, CancellationToken Token)
        {
            public TaskCompletionSource<Dictionary<string, List<CommitBuildInfo>>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class QueuedAdapter : IBuildServerAdapter
        {
            public string Name => "Test";
            public Channel<PendingQuery> Queries { get; } = Channel.CreateUnbounded<PendingQuery>();
            public int QueryCount;
            public int DisposeCount;
            public int MaxActiveQueries;
            private int _activeQueries;

            public void Initialize(BuildServerIntegration config, string repositoryPath) { }

            public async Task<Dictionary<string, List<CommitBuildInfo>>> QueryBuildStatusAsync(List<string> commits, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref QueryCount);
                var active = Interlocked.Increment(ref _activeQueries);
                if (active > MaxActiveQueries)
                    MaxActiveQueries = active;
                var query = new PendingQuery(commits, cancellationToken);
                Queries.Writer.TryWrite(query);
                try
                {
                    // Deliberately ignore cancellation to exercise late responses.
                    return await query.Result.Task;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeQueries);
                }
            }

            public void Dispose() => Interlocked.Increment(ref DisposeCount);
        }

        private sealed class BlockingAdapter : IBuildServerAdapter, IDisposable
        {
            public string Name => "Test";
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<Dictionary<string, List<CommitBuildInfo>>> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationToken Token { get; private set; }

            public void Initialize(BuildServerIntegration config, string repositoryPath) { }

            public Task<Dictionary<string, List<CommitBuildInfo>>> QueryBuildStatusAsync(List<string> commits) =>
                QueryBuildStatusAsync(commits, CancellationToken.None);

            public async Task<Dictionary<string, List<CommitBuildInfo>>> QueryBuildStatusAsync(List<string> commits, CancellationToken cancellationToken)
            {
                Token = cancellationToken;
                Entered.TrySetResult();
                return await Release.Task.WaitAsync(cancellationToken);
            }

            public void Dispose() => Disposed.TrySetResult();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// The tests link the network and polling code without starting Avalonia or
// accessing the real credential store. Unexpected UI or credential calls fail.
namespace Avalonia.Threading
{
    internal static class Dispatcher
    {
        internal static TestDispatcher UIThread { get; } = new();
    }

    internal sealed class TestDispatcher
    {
        public Task<T> InvokeAsync<T>(Func<Task<T>> callback) =>
            throw new InvalidOperationException("The test must not open a UI.");
    }
}

namespace SourceGit
{
    internal static class App
    {
        public static Task<Models.BuildServerCredentials> AskBuildServerCredentialsAsync(string server) =>
            throw new InvalidOperationException("The test must not request credentials.");
    }
}

namespace SourceGit.Utils
{
    internal static class CredentialStore
    {
        public static bool TryGet(string repository, string type, string server, out string user, out string secret) =>
            throw new InvalidOperationException("The test must not read credentials.");

        public static void Set(string repository, string type, string server, string user, string secret) =>
            throw new InvalidOperationException("The test must not write credentials.");
    }
}

namespace SourceGit.Models
{
    // Only the data that the poller uses; no repository or UI is needed.
    public sealed class Commit
    {
        public string SHA { get; set; }
        public List<CommitBuildInfo> BuildInfos { get; set; }
    }
}

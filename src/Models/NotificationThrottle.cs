using System;
using System.Collections.Immutable;
using System.Linq;

namespace SourceGit.Models
{
    internal sealed record NotificationThrottle
    {
        public ImmutableDictionary<(string Group, string Message), TimeSpan> RecentErrors { get; private init; } =
            ImmutableDictionary<(string Group, string Message), TimeSpan>.Empty;

        public (bool ShouldSend, NotificationThrottle State) Check(string group, string message, TimeSpan now)
        {
            var key = (group ?? string.Empty, message);
            if (RecentErrors.TryGetValue(key, out var lastShown) && now - lastShown < s_repeatInterval)
                return (false, this);

            // Only displayed errors start a new window. Repeated errors do not extend it.
            // Remove expired entries so that old errors do not stay in memory.
            var recentErrors = RecentErrors
                .RemoveRange(RecentErrors.Where(entry => now - entry.Value >= s_repeatInterval).Select(entry => entry.Key))
                .SetItem(key, now);

            return (true, this with { RecentErrors = recentErrors });
        }

        private static readonly TimeSpan s_repeatInterval = TimeSpan.FromMinutes(5);
    }
}

using System;
using System.Diagnostics;

namespace SourceGit.Models
{
    public class Notification
    {
        public static event Action<Notification> Raised;

        public string Group { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }

        public static void Send(string group, string message, bool isError = false)
        {
            var raised = Raised;
            if (raised == null)
                return;

            if (isError)
            {
                lock (s_errorLock)
                {
                    var result = s_errorThrottle.Check(group, message, s_clock.Elapsed);
                    s_errorThrottle = result.State;
                    if (!result.ShouldSend)
                        return;
                }
            }

            // Handlers can dispatch to the UI thread. Do not hold the lock here.
            raised.Invoke(new Notification
            {
                Group = group,
                Message = message,
                IsError = isError
            });
        }

        private static readonly object s_errorLock = new();
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static NotificationThrottle s_errorThrottle = new();
    }
}

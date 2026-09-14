using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using SourceGit.Models;

namespace SourceGit.NotificationTests
{
    internal static class Program
    {
        private static int Main()
        {
            var tests = new (string Name, Action Run)[]
            {
                (nameof(RepeatedErrorsAreSuppressed), RepeatedErrorsAreSuppressed),
                (nameof(AlternatingErrorsAreSuppressed), AlternatingErrorsAreSuppressed),
                (nameof(DifferentGroupsAreIndependent), DifferentGroupsAreIndependent),
                (nameof(InformationMessagesAreNotThrottled), InformationMessagesAreNotThrottled),
                (nameof(ConcurrentErrorsAreSuppressed), ConcurrentErrorsAreSuppressed),
                (nameof(UnobservedErrorsDoNotStartTheThrottle), UnobservedErrorsDoNotStartTheThrottle),
                (nameof(HandlersRunOutsideTheThrottleLock), HandlersRunOutsideTheThrottleLock),
                (nameof(RepeatWindowDoesNotSlide), RepeatWindowDoesNotSlide),
                (nameof(PollingErrorsStayQuietForFiveMinutes), PollingErrorsStayQuietForFiveMinutes),
                (nameof(ExpiredErrorsAreRemoved), ExpiredErrorsAreRemoved),
                (nameof(EmptyGroupsShareTheSameThrottle), EmptyGroupsShareTheSameThrottle),
                (nameof(ThrottleDoesNotChangeItsInputState), ThrottleDoesNotChangeItsInputState),
            };

            int failures = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Run();
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

        private static void RepeatedErrorsAreSuppressed()
        {
            var group = Guid.NewGuid().ToString();
            var messages = Capture(() =>
            {
                for (int i = 0; i < 10; i++)
                    Notification.Send(group, "Jenkins server is not reachable", true);
            });

            Equal(1, messages.Count);
            Equal(group, messages[0].Group);
            Equal("Jenkins server is not reachable", messages[0].Message);
            Equal(true, messages[0].IsError);
        }

        private static void AlternatingErrorsAreSuppressed()
        {
            var group = Guid.NewGuid().ToString();
            var messages = Capture(() =>
            {
                Notification.Send(group, "Server is not reachable", true);
                Notification.Send(group, "Request timed out", true);
                Notification.Send(group, "Server is not reachable", true);
                Notification.Send(group, "Request timed out", true);
            });

            Equal(2, messages.Count);
        }

        private static void DifferentGroupsAreIndependent()
        {
            var messages = Capture(() =>
            {
                Notification.Send(Guid.NewGuid().ToString(), "Server is not reachable", true);
                Notification.Send(Guid.NewGuid().ToString(), "Server is not reachable", true);
            });

            Equal(2, messages.Count);
        }

        private static void InformationMessagesAreNotThrottled()
        {
            var group = Guid.NewGuid().ToString();
            var messages = Capture(() =>
            {
                Notification.Send(group, "Same message");
                Notification.Send(group, "Same message");
                Notification.Send(group, "Same message", true);
                Notification.Send(group, "Same message");
            });

            Equal(4, messages.Count);
        }

        private static void ConcurrentErrorsAreSuppressed()
        {
            var group = Guid.NewGuid().ToString();
            var messages = Capture(() => Parallel.For(0, 100, _ =>
                Notification.Send(group, "Server is not reachable", true)));

            Equal(1, messages.Count);
        }

        private static void UnobservedErrorsDoNotStartTheThrottle()
        {
            var group = Guid.NewGuid().ToString();
            Notification.Send(group, "Server is not reachable", true);
            var messages = Capture(() => Notification.Send(group, "Server is not reachable", true));
            Equal(1, messages.Count);
        }

        private static void HandlersRunOutsideTheThrottleLock()
        {
            var group = Guid.NewGuid().ToString();
            void OnRaised(Notification notification)
            {
                if (notification.Group == group && notification.Message == "First error")
                {
                    var task = Task.Run(() => Notification.Send(group, "Second error", true));
                    if (!task.Wait(TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("A notification handler blocked the throttle.");
                }
            }

            Notification.Raised += OnRaised;
            try
            {
                var messages = Capture(() => Notification.Send(group, "First error", true));
                Equal(2, messages.Count);
            }
            finally
            {
                Notification.Raised -= OnRaised;
            }
        }

        private static void RepeatWindowDoesNotSlide()
        {
            var first = new NotificationThrottle().Check("repo", "error", TimeSpan.Zero);
            Equal(true, first.ShouldSend);

            var repeated = first.State.Check("repo", "error", TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Equal(false, repeated.ShouldSend);

            var reminder = repeated.State.Check("repo", "error", TimeSpan.FromMinutes(5));
            Equal(true, reminder.ShouldSend);
            Equal(false, reminder.State.Check("repo", "error", TimeSpan.FromMinutes(5)).ShouldSend);
        }

        private static void PollingErrorsStayQuietForFiveMinutes()
        {
            var state = new NotificationThrottle();
            int shown = 0;
            for (int seconds = 0; seconds <= 600; seconds += 120)
            {
                var result = state.Check("repo", "Jenkins server is not reachable", TimeSpan.FromSeconds(seconds));
                state = result.State;
                if (result.ShouldSend)
                    shown++;
            }

            Equal(2, shown);
        }

        private static void ExpiredErrorsAreRemoved()
        {
            var first = new NotificationThrottle().Check("repo", "old error", TimeSpan.Zero);
            var second = first.State.Check("repo", "recent error", TimeSpan.FromMinutes(4));
            var third = second.State.Check("repo", "new error", TimeSpan.FromMinutes(5));

            Equal(2, third.State.RecentErrors.Count);
            Equal(false, third.State.RecentErrors.ContainsKey(("repo", "old error")));
            Equal(false, third.State.Check("repo", "recent error", TimeSpan.FromMinutes(5)).ShouldSend);
        }

        private static void EmptyGroupsShareTheSameThrottle()
        {
            var first = new NotificationThrottle().Check(null, "error", TimeSpan.Zero);
            Equal(false, first.State.Check(string.Empty, "error", TimeSpan.Zero).ShouldSend);
        }

        private static void ThrottleDoesNotChangeItsInputState()
        {
            var original = new NotificationThrottle();
            var result = original.Check("repo", "error", TimeSpan.Zero);
            Equal(0, original.RecentErrors.Count);
            Equal(1, result.State.RecentErrors.Count);
            Equal(true, original.Check("repo", "error", TimeSpan.Zero).ShouldSend);
        }

        private static List<Notification> Capture(Action send)
        {
            var messages = new ConcurrentQueue<Notification>();
            Notification.Raised += messages.Enqueue;
            try
            {
                send();
                return new List<Notification>(messages);
            }
            finally
            {
                Notification.Raised -= messages.Enqueue;
            }
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}

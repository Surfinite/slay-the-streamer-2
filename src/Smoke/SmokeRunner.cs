// SMOKE-TEST: DELETE AFTER VALIDATION.
using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Smoke;

public static class SmokeRunner {
    public static async Task RunSmokeA(FakeChatService chat) =>
        await Run(chat, "smoke-A", "alice", "alice-id", waitForPrior: false);

    public static async Task RunSmokeB(FakeChatService chat) =>
        await Run(chat, "smoke-B", "bob", "bob-id", waitForPrior: true);

    /// <summary>Smoke C: blocking-await variant. Validates Plan B's realistic pattern.</summary>
    public static int RunSmokeCBlocking(FakeChatService chat) {
        try {
            // Wait for prior smokes to complete (avoid VoteCoordinator single-session race).
            ModEntry.SmokeATask.GetAwaiter().GetResult();
            Log.Info("[smoke-C] starting (BLOCKING prefix)...");
            var session = Voter.Start("smoke-C", new[] { "A", "B", "C" }, TimeSpan.FromSeconds(3));
            // ChatMessage positional ctor:
            //   (UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip)
            chat.Inject(new ChatMessage(
                UserId: "carol-id", Login: "carol", DisplayName: "carol", Text: "#0",
                ReceivedAt: DateTimeOffset.UtcNow,
                IsSubscriber: false, IsModerator: false, IsVip: false));
            // The scary line: synchronously block the Godot main thread waiting for the
            // vote to complete. If Plan A's RunContinuationsAsynchronously design is
            // correct, this returns winner=0 in ~3s. If the dispatcher deadlocks, the
            // game hangs at this call.
            var winner = session.AwaitWinnerAsync().GetAwaiter().GetResult();
            Log.Info($"[smoke-C] BLOCKING winner={winner} (expected 0)");
            return winner;
        } catch (Exception e) {
            Log.Error($"[smoke-C] FAILED: {e}");
            return -1;
        }
    }

    private static async Task Run(FakeChatService chat, string label, string login,
                                  string userId, bool waitForPrior) {
        try {
            if (waitForPrior) {
                Log.Info($"[{label}] waiting for prior smoke to complete...");
                await ModEntry.SmokeATask;
            }
            Log.Info($"[{label}] starting (duration=3s, options=A,B,C)...");
            var session = Voter.Start(label, new[] { "A", "B", "C" }, TimeSpan.FromSeconds(3));
            chat.Inject(new ChatMessage(
                UserId: userId, Login: login, DisplayName: login, Text: "#0",
                ReceivedAt: DateTimeOffset.UtcNow,
                IsSubscriber: false, IsModerator: false, IsVip: false));

            // Watchdog: if AwaitWinnerAsync hangs, log a timeout instead of silent freeze.
            var winnerTask = session.AwaitWinnerAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
            var completed = await Task.WhenAny(winnerTask, timeoutTask);
            if (completed != winnerTask) {
                Log.Error($"[{label}] TIMEOUT: AwaitWinnerAsync did not complete within 15s");
                return;
            }

            var winner = await winnerTask;
            Log.Info($"[{label}] winner={winner} (expected 0) " +
                $"(continuation thread={System.Environment.CurrentManagedThreadId}, " +
                $"main thread={ModEntry.GodotMainThreadId})");
        } catch (Exception e) {
            Log.Error($"[{label}] FAILED: {e}");
        }
    }
}

using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Tests.Voting;

public abstract class VoteSessionTestBase {
    protected readonly FakeClock Clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    protected readonly FakeTimerScheduler Scheduler;
    protected readonly ImmediateDispatcher Dispatcher = new();
    protected readonly FakeChatService Chat = new();
    protected readonly Random Rng = new(42);   // seeded so tie-break tests are deterministic

    protected VoteSessionTestBase() {
        Scheduler = new FakeTimerScheduler(Clock);
        Chat.ConnectAsync("test", new ChatCredentials("bot", "abc")).GetAwaiter().GetResult();
    }

    protected VoteSession StartVote(
        string label = "card reward",
        TimeSpan? duration = null,
        VoteParsingPolicy? parsing = null,
        VoteReceiptPolicy? receipts = null,
        params string[] options) {
        var opts = options.Length == 0 ? new[] { "Bash", "Defend", "Strike" } : options;
        var optionList = new List<VoteOption>();
        for (int i = 0; i < opts.Length; i++) optionList.Add(new VoteOption(i, opts[i]));

        return new VoteSession(
            id: $"{label}-test",
            label: label,
            options: optionList,
            duration: duration ?? TimeSpan.FromSeconds(30),
            chat: Chat,
            clock: Clock,
            scheduler: Scheduler,
            dispatcher: Dispatcher,
            random: Rng,
            parsingPolicy: parsing ?? VoteParsingPolicy.Default,
            receiptPolicy: receipts ?? VoteReceiptPolicy.Default,
            formatReceipt: null);
    }

    protected void Inject(string user, string text, string? userId = null) {
        userId ??= $"id-{user}";
        Chat.Inject(new ChatMessage(
            userId, user, user, text, Clock.UtcNow, false, false, false));
    }
}

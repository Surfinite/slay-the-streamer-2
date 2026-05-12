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
        string[]? options = null,
        IReadOnlyList<string>? configuredPlatforms = null) {
        // StartVote keeps the simple-substitute behaviour because no existing
        // tests rely on threading null through it.
        var opts = options ?? new[] { "Bash", "Defend", "Strike" };
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
            formatReceipt: null,
            configuredPlatforms: configuredPlatforms ?? new[] { ChatPlatformNames.Twitch },
            voteId: 0);
    }

    protected VoteSession CreateSession(
        int voteId = 0,
        IReadOnlyList<string>? configuredPlatforms = null,
        string label = "card reward",
        TimeSpan? duration = null,
        VoteParsingPolicy? parsing = null,
        VoteReceiptPolicy? receipts = null,
        string[]? options = null) {
        var opts = options ?? new[] { "Bash", "Defend", "Strike" };
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
            formatReceipt: null,
            configuredPlatforms: configuredPlatforms ?? new[] { ChatPlatformNames.Twitch },
            voteId: voteId);
    }

    /// <summary>
    /// Variant that forwards the configuredPlatforms argument verbatim
    /// without substituting a default. Used by tests that need to exercise
    /// the constructor's null-validation path.
    /// </summary>
    protected VoteSession CreateSessionRaw(
        IReadOnlyList<string>? configuredPlatforms,
        int voteId = 0,
        string label = "card reward",
        TimeSpan? duration = null,
        VoteParsingPolicy? parsing = null,
        VoteReceiptPolicy? receipts = null,
        string[]? options = null) {
        var opts = options ?? new[] { "Bash", "Defend", "Strike" };
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
            formatReceipt: null,
            configuredPlatforms: configuredPlatforms!,
            voteId: voteId);
    }

    protected VoteCoordinator CreateCoordinator(IReadOnlyList<string>? configuredPlatforms = null) =>
        new(Chat, configuredPlatforms ?? new[] { ChatPlatformNames.Twitch }, Clock, Scheduler, Dispatcher, Rng);

    protected void Inject(string user, string text, string? userId = null) {
        userId ??= $"id-{user}";
        Chat.Inject(new ChatMessage(
            userId, user, user, text, Clock.UtcNow, false, false, false));
    }

    /// <summary>
    /// Simulate a Twitch chat vote: builds a ChatMessage with the supplied
    /// numeric UserId and a "#optionIndex" text payload, then delivers it via
    /// the underlying FakeChatService so the session's OnChatMessage handler
    /// observes it the same way real Twitch traffic would.
    /// </summary>
    protected void InjectTwitchVote(VoteSession session, string userId, int optionIndex) {
        // session parameter present to make call sites readable; the test base
        // owns the single FakeChatService both this helper and the session
        // share.
        _ = session;
        Chat.Inject(new ChatMessage(
            UserId: userId,
            Login: $"login_{userId}",
            DisplayName: $"login_{userId}",
            Text: $"#{optionIndex}",
            ReceivedAt: Clock.UtcNow,
            IsSubscriber: false,
            IsModerator: false,
            IsVip: false));
    }

    /// <summary>
    /// Simulate a Twitch chat vote with a raw text payload (e.g. "#1!42" to
    /// exercise vote-nonce parsing). The session parameter exists for
    /// readability — the base owns the FakeChatService both sides share.
    /// </summary>
    protected void InjectTwitchVoteText(VoteSession session, string userId, string text) {
        _ = session;
        Chat.Inject(new ChatMessage(
            UserId: userId,
            Login: $"login_{userId}",
            DisplayName: $"login_{userId}",
            Text: text,
            ReceivedAt: Clock.UtcNow,
            IsSubscriber: false,
            IsModerator: false,
            IsVip: false));
    }

    /// <summary>
    /// Simulate a YouTube chat vote: UserId is prefixed with "yt:" per the
    /// D9 contract (ChatMessage.VoterKey then resolves to "yt:&lt;channelId&gt;"
    /// and VoteSession.PlatformOf classifies it as YouTube).
    /// </summary>
    protected void InjectYouTubeVote(VoteSession session, string channelId, int optionIndex) {
        _ = session;
        Chat.Inject(new ChatMessage(
            UserId: $"yt:{channelId}",
            Login: channelId,
            DisplayName: channelId,
            Text: $"#{optionIndex}",
            ReceivedAt: Clock.UtcNow,
            IsSubscriber: false,
            IsModerator: false,
            IsVip: false));
    }
}

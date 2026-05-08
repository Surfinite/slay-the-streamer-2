using System;
using System.Collections.Generic;
using System.Threading;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Owner of the per-streamer-channel vote lifecycle. One instance per
/// IChatService. Holds the currently-active VoteSession and enforces the
/// "strictly one open vote per coordinator" invariant.
/// </summary>
public sealed class VoteCoordinator : IDisposable {
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;

    public IChatService Chat => _chat;
    public VoteSession? CurrentSession { get; private set; }

    public VoteCoordinator(
        IChatService chat,
        IClock clock,
        ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher,
        Random? random = null) {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _random = random ?? new Random();
    }

    public VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt = null,
        CancellationToken ct = default) {

        if (CurrentSession is { State: VoteSessionState.Open })
            throw new InvalidOperationException(
                $"VoteCoordinator already has an open session ({CurrentSession.Id}); dispose/close it first.");

        var optionList = new List<VoteOption>(options.Count);
        for (int i = 0; i < options.Count; i++)
            optionList.Add(new VoteOption(i, options[i]));

        var id = $"{Slug(label)}-{_clock.UtcNow:yyyyMMddTHHmmssfff}";
        var session = new VoteSession(
            id: id, label: label, options: optionList, duration: duration,
            chat: _chat, clock: _clock, scheduler: _scheduler,
            dispatcher: _dispatcher, random: _random,
            parsingPolicy: parsing ?? VoteParsingPolicy.Default,
            receiptPolicy: receipts ?? VoteReceiptPolicy.Default,
            formatReceipt: formatReceipt);

        CurrentSession = session;
        session.Closed += OnSessionEnded;
        session.Cancelled += OnSessionEnded;

        return session;
    }

    private void OnSessionEnded(object? sender, VoteSession s) {
        if (CurrentSession == s) CurrentSession = null;
    }

    private static string Slug(string s) {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
            chars[i] = char.IsLetterOrDigit(s[i]) ? char.ToLowerInvariant(s[i]) : '-';
        return new string(chars);
    }

    public void Dispose() {
        CurrentSession?.Cancel();
        CurrentSession = null;
    }
}

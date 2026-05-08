using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

public sealed class VoteSession : IDisposable {
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;
    private readonly VoteParsingPolicy _parsing;
    private readonly VoteReceiptPolicy _receipts;
    private readonly Func<VoteSnapshot, ReceiptKind, string>? _formatReceipt;

    private readonly DateTimeOffset _openedAt;
    private readonly Dictionary<int, int> _tallies;
    private readonly Dictionary<string, int> _votersByKey = new();
    private readonly Regex _voteRegex;
    private readonly IDisposable _closeTimer;
    private VoteSessionState _state = VoteSessionState.Open;
    private int? _tieAmong;
    private bool _noVotesReceived;

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public VoteSessionState State => _state;
    public int? WinnerIndex { get; private set; }
    public TimeSpan TimeRemaining => MaxZero(_openedAt + Duration - _clock.UtcNow);
    public IReadOnlyDictionary<int, int> Tallies => new Dictionary<int, int>(_tallies);

    public event EventHandler<VoteSession>? TallyChanged;
    public event EventHandler<VoteSession>? Closed;
    public event EventHandler<VoteSession>? Cancelled;

    internal VoteSession(
        string id, string label, IReadOnlyList<VoteOption> options, TimeSpan duration,
        IChatService chat, IClock clock, ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher, Random random,
        VoteParsingPolicy parsingPolicy, VoteReceiptPolicy receiptPolicy,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt) {

        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        if (options is null || options.Count == 0) throw new ArgumentException("at least one option required", nameof(options));
        if (options.Count > 10) throw new ArgumentException("max 10 options (0..9)", nameof(options));
        for (int i = 0; i < options.Count; i++) {
            if (string.IsNullOrWhiteSpace(options[i].Label))
                throw new ArgumentException($"option {i} has empty label", nameof(options));
            if (options[i].Index != i)
                throw new ArgumentException($"option {i} has wrong Index ({options[i].Index})", nameof(options));
        }
        if (duration < TimeSpan.FromSeconds(1)) throw new ArgumentException("duration must be >= 1s", nameof(duration));

        Id = id; Label = label; Options = options; Duration = duration;
        _chat = chat; _clock = clock; _scheduler = scheduler; _dispatcher = dispatcher;
        _random = random; _parsing = parsingPolicy; _receipts = receiptPolicy;
        _formatReceipt = formatReceipt;

        _openedAt = clock.UtcNow;
        _tallies = options.ToDictionary(o => o.Index, _ => 0);
        _voteRegex = BuildRegex(parsingPolicy);

        _chat.MessageReceived += OnChatMessage;
        _closeTimer = scheduler.Schedule(duration, () => _dispatcher.Post(() => CloseNowInternal(byTimer: true)));
    }

    private static Regex BuildRegex(VoteParsingPolicy p) {
        var prefix = (p.AcceptHashCommands, p.AcceptBangCommands) switch {
            (true, true) => "[#!]?",
            (true, false) => "#?",
            (false, true) => "!?",
            _ => ""
        };
        return new Regex($@"^{prefix}(\d+)(?:\s|$)", RegexOptions.Compiled);
    }

    private void OnChatMessage(object? sender, ChatMessage msg) {
        if (_state != VoteSessionState.Open) return;
        var match = _voteRegex.Match(msg.Text);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var idx)) return;
        if (idx < 0 || idx >= Options.Count) return;

        var key = msg.VoterKey;
        if (_votersByKey.TryGetValue(key, out var prior)) {
            if (prior == idx) return;
            _tallies[prior]--;
        }
        _votersByKey[key] = idx;
        _tallies[idx]++;
        TallyChanged?.Invoke(this, this);
    }

    public int CloseNow() {
        if (_state is VoteSessionState.Closed or VoteSessionState.Cancelled or VoteSessionState.Disposed)
            return WinnerIndex ?? 0;
        return CloseNowInternal(byTimer: false);
    }

    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _state = VoteSessionState.Closed;
        Closed?.Invoke(this, this);
        return winner;
    }

    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _state = VoteSessionState.Cancelled;
        Cancelled?.Invoke(this, this);
    }

    internal (int Winner, int? TieAmong, bool NoVotes) ComputeWinnerForTest() => ComputeWinner();

    private (int Winner, int? TieAmong, bool NoVotes) ComputeWinner() {
        var voted = _tallies.Where(kv => kv.Value > 0).ToList();
        if (voted.Count == 0) {
            var idx = _random.Next(Options.Count);
            return (idx, null, true);
        }
        var maxCount = voted.Max(kv => kv.Value);
        var tied = voted.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
        if (tied.Count == 1) return (tied[0], null, false);
        return (tied[_random.Next(tied.Count)], tied.Count, false);
    }

    public VoteSnapshot Snapshot() => new(
        Id: Id, Label: Label, Options: Options,
        Duration: Duration, TimeRemaining: TimeRemaining,
        Tallies: new Dictionary<int, int>(_tallies),
        State: _state, WinnerIndex: WinnerIndex,
        RandomTieAmong: _tieAmong, NoVotesReceived: _noVotesReceived,
        DisconnectGap: TimeSpan.Zero);

    public void Dispose() {
        if (_state == VoteSessionState.Disposed) return;
        if (_state == VoteSessionState.Open) Cancel();
        _state = VoteSessionState.Disposed;
    }

    private static TimeSpan MaxZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}

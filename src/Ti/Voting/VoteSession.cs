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
    private readonly IDisposable? _periodicTimer;
    private string? _lastPeriodicTallyKey;   // dedup key derived from tally state, not rendered text
    private VoteSessionState _state = VoteSessionState.Open;
    private int? _tieAmong;
    private bool _noVotesReceived;

    private readonly System.Threading.Tasks.TaskCompletionSource<int> _winnerTcs =
        new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _anyoneAwaited;

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

        // Periodic tally cadence (adaptive default)
        var cadence = ResolveCadence(_receipts.PeriodicTallyEvery, duration);
        if (cadence > TimeSpan.Zero)
            _periodicTimer = scheduler.SchedulePeriodic(cadence, () => _dispatcher.Post(SendPeriodicReceipt));

        // Open receipt
        if (_receipts.AnnounceOnOpen) {
            _ = SendReceipt(ReceiptKind.Open, OutgoingMessagePriority.Normal);
        }
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
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        if (_receipts.AnnounceOnClose) {
            _ = SendReceipt(ReceiptKind.Close, OutgoingMessagePriority.High);
        }
        _winnerTcs.TrySetResult(winner);
        if (!_anyoneAwaited)
            TiLog.Warn($"VoteSession {Id} closed with winner {winner} but AwaitWinnerAsync was never called — caller likely forgot to consume the result.");
        Closed?.Invoke(this, this);
        return winner;
    }

    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Cancelled;
        _winnerTcs.TrySetCanceled();
        Cancelled?.Invoke(this, this);
    }

    public async System.Threading.Tasks.Task<int> AwaitWinnerAsync(System.Threading.CancellationToken ct = default) {
        _anyoneAwaited = true;
        using var reg = ct.Register(() => { /* token-cancellation cancels only this awaiter, not the session */ });
        var winnerTask = _winnerTcs.Task;
        var canceledTask = System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, ct);
        var done = await System.Threading.Tasks.Task.WhenAny(winnerTask, canceledTask).ConfigureAwait(false);
        if (done == canceledTask) ct.ThrowIfCancellationRequested();
        // Session-cancellation surfaces as TaskCanceledException from the TCS;
        // normalise to OperationCanceledException for callers that match exactly.
        if (winnerTask.IsCanceled) throw new System.OperationCanceledException();
        return await winnerTask.ConfigureAwait(false);
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

    private static TimeSpan ResolveCadence(TimeSpan? configured, TimeSpan duration) {
        if (configured is null) {
            // adaptive: max(7s, duration/5). The 7s floor (rather than 5s)
            // avoids the periodic timer firing at the same instant as the
            // close timer for very short votes — for a 5s vote, the periodic
            // timer at t=7 fires after close has already disposed it (no-op),
            // sidestepping a brittle scheduler-ordering dependency.
            var adaptive = TimeSpan.FromSeconds(Math.Max(7.0, duration.TotalSeconds / 5.0));
            return adaptive;
        }
        return configured.Value;   // TimeSpan.Zero disables
    }

    private void SendPeriodicReceipt() {
        if (_state != VoteSessionState.Open) return;
        // Belt-and-braces guard for the case where a custom fixed cadence
        // matches the vote duration exactly and the periodic timer fires
        // before the close timer at the same instant. The 7s adaptive floor
        // handles the auto case; this handles any custom cadence corner.
        if (TimeRemaining < TimeSpan.FromSeconds(1)) return;
        if (_tallies.Values.All(c => c == 0)) return;            // skip when all zero
        // Dedup on the tally STATE, not the rendered text. The rendered text
        // contains "<remaining>s left" which differs every tick even when the
        // tallies are identical, so a text-equality dedup would never trigger.
        var tallyKey = string.Join(",", _tallies.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        if (tallyKey == _lastPeriodicTallyKey) return;
        _lastPeriodicTallyKey = tallyKey;
        var text = FormatReceipt(ReceiptKind.PeriodicTally);
        _ = _chat.SendMessageAsync(text, OutgoingMessagePriority.Low);
    }

    private System.Threading.Tasks.Task SendReceipt(ReceiptKind kind, OutgoingMessagePriority priority) {
        var text = FormatReceipt(kind);
        return _chat.SendMessageAsync(text, priority);
    }

    private string FormatReceipt(ReceiptKind kind) {
        if (_formatReceipt is not null) return _formatReceipt(Snapshot(), kind);
        return kind switch {
            ReceiptKind.Open => EnglishReceipts.FormatOpen(Snapshot()),
            ReceiptKind.PeriodicTally => EnglishReceipts.FormatPeriodicTally(Snapshot()),
            ReceiptKind.Close => EnglishReceipts.FormatClose(Snapshot()),
            _ => string.Empty,
        };
    }

    private static TimeSpan MaxZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}

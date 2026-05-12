using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

public sealed class VoteSession : IDisposable {
    private readonly IChatConsumer _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;
    private readonly VoteParsingPolicy _parsing;
    private readonly VoteReceiptPolicy _receipts;
    private readonly Func<VoteSnapshot, ReceiptKind, string>? _formatReceipt;

    private const int MaxVoters = 10_000;
    private bool _voterCapWarnLogged;

    private readonly DateTimeOffset _openedAt;
    private readonly Dictionary<int, int> _tallies;
    private readonly Dictionary<string, int> _votersByKey = new();
    private readonly IReadOnlyList<string> _configuredPlatforms;
    private readonly Dictionary<(string Platform, int OptionIndex), int> _talliesByPlatform = new();
    private readonly Dictionary<string, DateTimeOffset> _lastVoteByPlatform = new();
    private readonly Regex _voteRegex;
    private readonly IDisposable _closeTimer;
    private readonly IDisposable? _periodicTimer;
    private string? _lastPeriodicTallyKey;   // dedup key derived from tally state, not rendered text
    private VoteSessionState _state = VoteSessionState.Open;
    private int? _tieAmong;
    private bool _noVotesReceived;
    private TimeSpan _disconnectGapAccum;
    private DateTimeOffset? _disconnectStartedAt;

    private readonly System.Threading.Tasks.TaskCompletionSource<int> _winnerTcs =
        new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _anyoneAwaited;

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public int VoteId { get; }
    public int TallyVersion { get; private set; }
    public VoteSessionState State => _state;
    public int? WinnerIndex { get; private set; }
    public TimeSpan TimeRemaining => MaxZero(_openedAt + Duration - _clock.UtcNow);
    public IReadOnlyDictionary<int, int> Tallies => new Dictionary<int, int>(_tallies);
    public IReadOnlyList<string> ConfiguredPlatforms => _configuredPlatforms;
    public IReadOnlyDictionary<(string Platform, int OptionIndex), int>? TalliesByPlatform =>
        _configuredPlatforms.Count > 1 ? _talliesByPlatform : null;
    public IReadOnlyDictionary<string, DateTimeOffset> LastVoteByPlatform => _lastVoteByPlatform;

    public event EventHandler<VoteSession>? TallyChanged;
    public event EventHandler<VoteSession>? Closed;
    public event EventHandler<VoteSession>? Cancelled;

    internal VoteSession(
        string id, string label, IReadOnlyList<VoteOption> options, TimeSpan duration,
        IChatConsumer chat, IClock clock, ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher, Random random,
        VoteParsingPolicy parsingPolicy, VoteReceiptPolicy receiptPolicy,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt,
        IReadOnlyList<string> configuredPlatforms,
        int voteId) {

        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        if (options is null || options.Count == 0) throw new ArgumentException("at least one option required", nameof(options));
        if (options.Count > 10) throw new ArgumentException("max 10 options (0..9)", nameof(options));
        for (int i = 0; i < options.Count; i++) {
            var normalized = StripControlChars(options[i].Label);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException($"option {i} has empty label after control-char strip", nameof(options));
            if (normalized.Length > 200)
                throw new ArgumentException($"option {i} label is > 200 chars after control-char strip", nameof(options));
            if (options[i].Index != i)
                throw new ArgumentException($"option {i} has wrong Index ({options[i].Index})", nameof(options));
        }
        if (duration < TimeSpan.FromSeconds(1)) throw new ArgumentException("duration must be >= 1s", nameof(duration));
        if (configuredPlatforms is null) throw new ArgumentNullException(nameof(configuredPlatforms));
        if (configuredPlatforms.Count == 0)
            throw new ArgumentException("configuredPlatforms must not be empty", nameof(configuredPlatforms));

        Id = id; Label = label; Options = options; Duration = duration;
        VoteId = voteId;
        _chat = chat; _clock = clock; _scheduler = scheduler; _dispatcher = dispatcher;
        _random = random; _parsing = parsingPolicy; _receipts = receiptPolicy;
        _formatReceipt = formatReceipt;
        _configuredPlatforms = configuredPlatforms;

        _openedAt = clock.UtcNow;
        _tallies = options.ToDictionary(o => o.Index, _ => 0);
        foreach (var platform in _configuredPlatforms)
            for (int i = 0; i < options.Count; i++)
                _talliesByPlatform[(platform, i)] = 0;
        _voteRegex = BuildRegex(parsingPolicy);

        _chat.MessageReceived += OnChatMessage;
        _chat.ConnectionStateChanged += OnChatConnectionStateChanged;
        if (!IsChatOnline(_chat.State)) _disconnectStartedAt = clock.UtcNow;
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

    private static bool IsChatOnline(ChatConnectionState state) =>
        state == ChatConnectionState.ConnectedReadOnly ||
        state == ChatConnectionState.ConnectedReadWrite;

    private void OnChatConnectionStateChanged(object? sender, ChatConnectionChangedEventArgs e) {
        if (_state != VoteSessionState.Open) return;
        var nowOnline = IsChatOnline(e.NewState);
        var wasOnline = IsChatOnline(e.OldState);
        if (wasOnline && !nowOnline) {
            _disconnectStartedAt = _clock.UtcNow;
        } else if (!wasOnline && nowOnline && _disconnectStartedAt is { } start) {
            _disconnectGapAccum += _clock.UtcNow - start;
            _disconnectStartedAt = null;
        }
    }

    private void OnChatMessage(object? sender, ChatMessage msg) {
        if (_state != VoteSessionState.Open) return;
        var match = _voteRegex.Match(msg.Text);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var idx)) return;
        if (idx < 0 || idx >= Options.Count) return;

        var key = msg.VoterKey;
        var existing = _votersByKey.TryGetValue(key, out var prior);
        if (!existing && _votersByKey.Count >= MaxVoters) {
            if (!_voterCapWarnLogged) {
                TiLog.Warn($"VoteSession {Id}: voter cap of {MaxVoters} reached; dropping further unique voters.");
                _voterCapWarnLogged = true;
            }
            return;
        }
        if (existing) {
            if (prior == idx) return;
            _tallies[prior]--;
        }
        _votersByKey[key] = idx;
        _tallies[idx]++;

        // Per-platform side-dict maintenance.
        // Platform is voter-stable (same VoterKey → same prefix → same platform),
        // so the prior vote's platform == current msg's platform.
        var platform = PlatformOf(msg);
        if (existing) {
            var priorKey = (platform, prior);
            if (_talliesByPlatform.TryGetValue(priorKey, out var priorCount) && priorCount > 0)
                _talliesByPlatform[priorKey] = priorCount - 1;
        }
        var nextKey = (platform, idx);
        _talliesByPlatform[nextKey] = _talliesByPlatform.TryGetValue(nextKey, out var nextCount)
            ? nextCount + 1
            : 1;
        _lastVoteByPlatform[platform] = _clock.UtcNow;

        TallyVersion++;
        TallyChanged?.Invoke(this, this);
    }

    private static string PlatformOf(ChatMessage msg) =>
        msg.VoterKey.StartsWith("yt:", StringComparison.Ordinal)
            ? ChatPlatformNames.YouTube
            : ChatPlatformNames.Twitch;

    public int CloseNow() {
        if (_state == VoteSessionState.Closed)
            return WinnerIndex!.Value;
        if (_state is VoteSessionState.Cancelled or VoteSessionState.Disposed)
            throw new InvalidOperationException(
                $"Cannot close VoteSession {Id} in state {_state}.");
        return CloseNowInternal(byTimer: false);
    }

    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        // Finalise any in-progress disconnect gap before computing the snapshot
        // used by the close receipt.
        if (_disconnectStartedAt is { } start) {
            _disconnectGapAccum += _clock.UtcNow - start;
            _disconnectStartedAt = null;
        }
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionStateChanged -= OnChatConnectionStateChanged;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        if (_receipts.AnnounceOnClose) {
            _ = SendReceipt(ReceiptKind.Close, OutgoingMessagePriority.High);
        }
        _winnerTcs.TrySetResult(winner);
        if (!_anyoneAwaited)
            TiLog.Warn($"VoteSession {Id} closed with winner {winner} but AwaitWinnerAsync was never called; caller likely forgot to consume the result.");
        Closed?.Invoke(this, this);
        return winner;
    }

    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionStateChanged -= OnChatConnectionStateChanged;
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

    public VoteSnapshot Snapshot() {
        var liveGap = _disconnectGapAccum;
        if (_disconnectStartedAt is { } s && _state == VoteSessionState.Open)
            liveGap += _clock.UtcNow - s;
        return new VoteSnapshot(
            Id: Id, Label: Label, Options: Options,
            Duration: Duration, TimeRemaining: TimeRemaining,
            Tallies: new Dictionary<int, int>(_tallies),
            State: _state, WinnerIndex: WinnerIndex,
            RandomTieAmong: _tieAmong, NoVotesReceived: _noVotesReceived,
            DisconnectGap: liveGap,
            VoteId: VoteId);
    }

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
        _ = SendReceipt(ReceiptKind.PeriodicTally, OutgoingMessagePriority.Low);
    }

    private System.Threading.Tasks.Task SendReceipt(ReceiptKind kind, OutgoingMessagePriority priority) {
        var text = FormatReceipt(kind);
        var t = _chat.SendMessageAsync(text, priority);
        return t.ContinueWith(
            x => TiLog.Error($"VoteSession {Id}: {kind} receipt send failed", x.Exception?.GetBaseException()),
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
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

    private static string StripControlChars(string s) {
        if (s.Length == 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString();
    }
}

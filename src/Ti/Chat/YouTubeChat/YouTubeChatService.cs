using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Read-only YouTube live-chat service. Polls the youtubei internal endpoint via
/// IYouTubeLiveChatScraper and surfaces messages as ChatMessage events. Send is
/// not supported (Decision D3). Reconnect cadence + 429 carve-out + 30-cycle
/// escalation receipt are added in Tasks 22, 23.
/// </summary>
public sealed class YouTubeChatService : IChatService, IFastPollable {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IYouTubeLiveBroadcastDiscovery _discovery;
    private readonly IYouTubeLiveChatScraper _scraper;

    private int _disposed;   // Interlocked
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private string? _channelId;
    private CancellationTokenSource? _cts;
    private string? _videoId;
    private string? _continuation;
    private string? _apiKey;
    private string? _clientVersion;
    private Task? _connectTask;
    private Task? _pollLoopTask;
    private IDisposable? _retryTimer;
    private int _consecutive429Count;
    private int _consecutiveReconnectCount;
    private bool _escalationSent;
    private volatile bool _fastPoll;
    private TaskCompletionSource _fastPollWake = NewWakeSignal();

    private const int EscalationThreshold = 30;

    private static readonly TimeSpan PollMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollMax = TimeSpan.FromSeconds(10);

    /// <summary>Poll interval while fast polling is on (vote-window cadence).
    /// Instance-settable so tests can shrink it below real-time scale.</summary>
    internal TimeSpan FastPollInterval { get; set; } = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBase = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReconnectJitter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectCap = TimeSpan.FromSeconds(600);

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.Reconnecting;
    public bool CanSend => false;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }
    public YouTubeChatStatusReason LastStatusReason { get; private set; } = YouTubeChatStatusReason.None;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<YouTubeEscalationRequestedEventArgs>? EscalationRequested;

    internal YouTubeChatService(
        IMainThreadDispatcher dispatcher,
        IClock clock,
        ITimerScheduler scheduler,
        IYouTubeLiveBroadcastDiscovery discovery,
        IYouTubeLiveChatScraper scraper) {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _scraper = scraper ?? throw new ArgumentNullException(nameof(scraper));
    }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        if (Volatile.Read(ref _disposed) == 1) return Task.CompletedTask;
        if (_state != ChatConnectionState.Disconnected) return Task.CompletedTask;
        _channelId = channel ?? throw new ArgumentNullException(nameof(channel));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TransitionTo(ChatConnectionState.Connecting, "ConnectAsync called", YouTubeChatStatusReason.None);
        _connectTask = Task.Run(() => RunConnectAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunConnectAsync(CancellationToken ct) {
        try {
            var videoId = await _discovery.FindLiveVideoIdAsync(_channelId!, ct).ConfigureAwait(false);
            if (videoId is null) {
                TransitionTo(ChatConnectionState.Reconnecting,
                    "no live broadcast found", YouTubeChatStatusReason.NoLiveBroadcastFound);
                // Reconnect timer arm comes in Task 22.
                return;
            }
            _videoId = videoId;

            var pageResult = await _scraper.ParseInitialPageAsync(videoId, ct).ConfigureAwait(false);
            if (pageResult is null) {
                TransitionTo(ChatConnectionState.Reconnecting,
                    "initial page parse failed", YouTubeChatStatusReason.ScraperParseFailed);
                return;
            }
            _apiKey = pageResult.InnertubeApiKey;
            _clientVersion = pageResult.ClientVersion;

            // Cursor-establishing poll: messages discarded.
            var cursorResult = await _scraper.PollAsync(
                _apiKey, _clientVersion, pageResult.InitialContinuation, ct).ConfigureAwait(false);
            if (cursorResult.NextContinuation is null) {
                TransitionTo(ChatConnectionState.Reconnecting,
                    "cursor-establishing poll returned no continuation",
                    YouTubeChatStatusReason.LiveBroadcastEnded);
                return;
            }
            if (cursorResult.Messages.Count > 0) {
                TiLog.Debug($"[YouTubeChatService] cursor-established; suppressed {cursorResult.Messages.Count} backlog messages");
            }
            _continuation = cursorResult.NextContinuation;

            TransitionTo(ChatConnectionState.ConnectedReadOnly,
                "initial connect succeeded", YouTubeChatStatusReason.None);

            // Start steady-state poll loop. Initial poll's NextTimeoutMs seeds the cadence.
            int seedTimeoutMs = cursorResult.NextTimeoutMs;
            _pollLoopTask = Task.Run(() => RunPollLoopAsync(seedTimeoutMs, ct));
        } catch (OperationCanceledException) {
            // expected on Disconnect/Dispose
        } catch (YouTubeHttpStatusException ex) {
            LastError = ex;
            var reason = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? YouTubeChatStatusReason.RateLimited
                : YouTubeChatStatusReason.NetworkError;
            TransitionTo(ChatConnectionState.Reconnecting,
                $"HTTP {(int)ex.StatusCode} during connect", reason);
        } catch (Exception ex) {
            LastError = ex;
            TransitionTo(ChatConnectionState.Reconnecting,
                $"connect failed: {ex.GetType().Name}", YouTubeChatStatusReason.NetworkError);
        }
    }

    public void Disconnect() {
        if (Volatile.Read(ref _disposed) == 1) return;
        try { _cts?.Cancel(); } catch { }
        TransitionTo(ChatConnectionState.Disconnected, "Disconnect called", YouTubeChatStatusReason.None);
    }

    /// <summary>
    /// While a vote is open the voting layer enables fast polling: the loop
    /// ignores YouTube's recommended timeoutMs and polls every
    /// <see cref="FastPollInterval"/>. Enabling also wakes an in-flight
    /// steady-state delay (which can be up to PollMax) so the first fast poll
    /// happens immediately rather than after the residual wait.
    /// </summary>
    public void SetFastPolling(bool enabled) {
        if (Volatile.Read(ref _disposed) == 1) return;
        if (_fastPoll == enabled) return;
        _fastPoll = enabled;
        TiLog.Info($"[YouTubeChatService] fast polling {(enabled ? "enabled" : "disabled")}");
        if (enabled) {
            Interlocked.Exchange(ref _fastPollWake, NewWakeSignal()).TrySetResult();
        }
    }

    private static TaskCompletionSource NewWakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunPollLoopAsync(int seedTimeoutMs, CancellationToken ct) {
        int lastTimeoutMs = seedTimeoutMs <= 0 ? 5000 : seedTimeoutMs;
        while (!ct.IsCancellationRequested && _state == ChatConnectionState.ConnectedReadOnly) {
            var clampedMs = Math.Clamp(lastTimeoutMs, (int)PollMin.TotalMilliseconds, (int)PollMax.TotalMilliseconds);
            // Acquire-read the wake signal FIRST (Volatile.Read pins the order; a
            // plain load could legally be reordered after the _fastPoll read and
            // miss a toggle). The writer publishes _fastPoll=true before full-fence
            // swapping in the new signal, so: observing the new signal implies
            // observing _fastPoll==true (fast delay used, wake unneeded); observing
            // the old one means the writer's TrySetResult on it wakes our WhenAny.
            var wake = Volatile.Read(ref _fastPollWake);
            var delayMs = _fastPoll ? (int)FastPollInterval.TotalMilliseconds : clampedMs;
            try {
                await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct), wake.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;
            if (Volatile.Read(ref _disposed) == 1) return;
            if (_state != ChatConnectionState.ConnectedReadOnly) return;

            try {
                var result = await _scraper.PollAsync(_apiKey!, _clientVersion!, _continuation!, ct).ConfigureAwait(false);
                if (result.NextContinuation is null) {
                    TransitionTo(ChatConnectionState.Reconnecting,
                        "poll returned no continuation; broadcast ended",
                        YouTubeChatStatusReason.LiveBroadcastEnded);
                    return;
                }
                foreach (var msg in result.Messages) {
                    if (Volatile.Read(ref _disposed) == 1) return;
                    var chatMessage = new ChatMessage(
                        UserId: $"yt:{msg.AuthorChannelId}",
                        Login: msg.AuthorDisplayName,
                        DisplayName: msg.AuthorDisplayName,
                        Text: msg.Text,
                        ReceivedAt: _clock.UtcNow,
                        IsSubscriber: msg.IsChatMember,
                        IsModerator: msg.IsChatModerator,
                        IsVip: false);
                    LastMessageReceivedAt = _clock.UtcNow;
                    var captured = chatMessage;
                    _dispatcher.Post(() => MessageReceived?.Invoke(this, captured));
                }
                _continuation = result.NextContinuation;
                lastTimeoutMs = result.NextTimeoutMs;
            } catch (OperationCanceledException) {
                return;
            } catch (YouTubeHttpStatusException ex) {
                LastError = ex;
                var reason = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? YouTubeChatStatusReason.RateLimited
                    : YouTubeChatStatusReason.NetworkError;
                TransitionTo(ChatConnectionState.Reconnecting,
                    $"poll HTTP {(int)ex.StatusCode}", reason);
                return;
            } catch (Exception ex) {
                LastError = ex;
                TransitionTo(ChatConnectionState.Reconnecting,
                    $"poll failed: {ex.GetType().Name}",
                    YouTubeChatStatusReason.NetworkError);
                return;
            }
        }
    }

    public Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("YouTubeChatService is read-only (D3)."));

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        try { _retryTimer?.Dispose(); } catch { }
        _retryTimer = null;
        if (_state != ChatConnectionState.Disposed) {
            var old = _state;
            _state = ChatConnectionState.Disposed;
            var args = new ChatConnectionChangedEventArgs(old, ChatConnectionState.Disposed, "Dispose");
            try { _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args)); } catch { }
        }
    }

    private void TransitionTo(
        ChatConnectionState next,
        string reason,
        YouTubeChatStatusReason statusReason) {
        if (_state == next && LastStatusReason == statusReason) return;
        var old = _state;
        _state = next;
        LastStatusReason = statusReason;
        TiLog.Info($"[YouTubeChatService] {old} -> {next}: {reason} (reason={statusReason})");
        var args = new ChatConnectionChangedEventArgs(old, next, reason);
        _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args));

        if (next == ChatConnectionState.ConnectedReadOnly) {
            _consecutive429Count = 0;
            _consecutiveReconnectCount = 0;
            _escalationSent = false;
        } else if (next == ChatConnectionState.Reconnecting) {
            ArmReconnect();
        }
    }

    private void ArmReconnect() {
        if (Volatile.Read(ref _disposed) == 1) return;
        _retryTimer?.Dispose();
        _consecutiveReconnectCount++;
        if (_consecutiveReconnectCount == EscalationThreshold && !_escalationSent) {
            _escalationSent = true;
            var escalationArgs = new YouTubeEscalationRequestedEventArgs(_consecutiveReconnectCount, LastStatusReason);
            try { EscalationRequested?.Invoke(this, escalationArgs); }
            catch (Exception ex) { TiLog.Error("[YouTubeChatService] EscalationRequested handler threw", ex); }
        }
        var delay = NextReconnectDelay(LastError);
        _retryTimer = _scheduler.Schedule(delay, () => {
            if (Volatile.Read(ref _disposed) == 1) return;
            // Clear ephemeral state and re-run connect flow.
            _videoId = null;
            _continuation = null;
            _apiKey = null;
            _clientVersion = null;
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
            TransitionTo(ChatConnectionState.Connecting, "reconnect timer fired", YouTubeChatStatusReason.None);
            _connectTask = Task.Run(() => RunConnectAsync(_cts.Token));
        });
    }

    private TimeSpan NextReconnectDelay(Exception? lastError) {
        if (lastError is YouTubeHttpStatusException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } ex) {
            _consecutive429Count++;
            if (ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero) {
                // Honor server-provided Retry-After + [0, +jitter) one-sided jitter.
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ReconnectJitter.TotalMilliseconds);
                return retryAfter + jitter;
            }
            // Exponential backoff: 60s -> 120s -> 240s -> 480s -> 600s (cap).
            var backoff = TimeSpan.FromSeconds(
                Math.Min(60 * Math.Pow(2, _consecutive429Count - 1), ReconnectCap.TotalSeconds));
            return ClampJitter(backoff);
        }
        _consecutive429Count = 0;
        return ClampJitter(ReconnectBase);
    }

    private static TimeSpan ClampJitter(TimeSpan baseDelay) {
        var jitterMs = (Random.Shared.NextDouble() - 0.5) * 2 * ReconnectJitter.TotalMilliseconds;
        var total = baseDelay + TimeSpan.FromMilliseconds(jitterMs);
        return total < TimeSpan.Zero ? TimeSpan.Zero : total;
    }
}

/// <summary>
/// Raised by YouTubeChatService when its consecutive-reconnect counter hits the
/// escalation threshold (30 consecutive failures, one-shot per burst). ModEntry
/// listens and surfaces the receipt via D8 wording.
/// </summary>
public sealed record YouTubeEscalationRequestedEventArgs(
    int ConsecutiveReconnectCount,
    YouTubeChatStatusReason LastStatusReason);

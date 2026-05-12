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
public sealed class YouTubeChatService : IChatService {
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

            // Steady-state poll loop starts in Task 21.
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
        // Implemented in Task 21.
    }

    public Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("YouTubeChatService is read-only (D3)."));

    public void Dispose() {
        Interlocked.Exchange(ref _disposed, 1);
        // Full teardown in Task 22.
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

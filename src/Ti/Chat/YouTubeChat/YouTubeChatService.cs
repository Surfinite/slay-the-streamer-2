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
        // Implemented in Task 20.
        _channelId = channel;
        return Task.CompletedTask;
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

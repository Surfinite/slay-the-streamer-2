using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat;

public sealed class TwitchIrcChatService : IChatService {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly Func<IIrcTransport> _transportFactory;
    private readonly int _sendCapacity;
    private readonly TimeSpan _sendWindow;
    private readonly TimeSpan _sendMinInterval;
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private bool _disposed;
    private IDisposable? _joinTimeoutTimer;
    private static readonly TimeSpan JoinConfirmationTimeout = TimeSpan.FromSeconds(10);
    private int _reconnectAttempt;
    private static readonly TimeSpan[] BackoffSeconds = {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)
    };

    private const string TwitchIrcHost = "irc.chat.twitch.tv";
    private const int TwitchIrcPort = 6697;

    private IIrcTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;
    private string? _selfLogin;
    private string? _channel;
    private ChatCredentials? _creds;
    private OutgoingMessageQueue? _sendQueue;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _state is ChatConnectionState.ConnectedReadWrite;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }
    internal bool HasTags { get; private set; } = true;   // optimistic; falsified by CAP NAK

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Production constructor.</summary>
    public TwitchIrcChatService(
        IMainThreadDispatcher dispatcher, IClock clock, ITimerScheduler scheduler,
        int sendCapacity, TimeSpan sendWindow)
        : this(dispatcher, clock, scheduler,
               transportFactory: () => new SslIrcTransport(),
               sendCapacity, sendWindow, sendMinInterval: TimeSpan.FromSeconds(1)) {
    }

    /// <summary>Internal constructor for tests — accepts a transport factory + custom timing.</summary>
    internal TwitchIrcChatService(
        IMainThreadDispatcher dispatcher, IClock clock, ITimerScheduler scheduler,
        Func<IIrcTransport> transportFactory,
        int sendCapacity, TimeSpan sendWindow, TimeSpan sendMinInterval) {
        _dispatcher = dispatcher;
        _clock = clock;
        _scheduler = scheduler;
        _transportFactory = transportFactory;
        _sendCapacity = sendCapacity;
        _sendWindow = sendWindow;
        _sendMinInterval = sendMinInterval;
    }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TwitchIrcChatService)));
        if (_state != ChatConnectionState.Disconnected) return Task.CompletedTask;

        _channel = NormaliseChannel(channel);
        _creds = creds;
        _selfLogin = creds?.Username ?? "justinfan" + Random.Shared.Next(1000, 9999);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transport = _transportFactory();

        TransitionTo(ChatConnectionState.Connecting, "ConnectAsync called");
        _readLoopTask = Task.Run(() => RunConnectionAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunConnectionAsync(CancellationToken ct) {
        try {
            await _transport!.ConnectAsync(TwitchIrcHost, TwitchIrcPort, ct);
            await _transport.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
            if (_creds is not null) {
                await _transport.WriteLineAsync($"PASS oauth:{_creds.OauthToken}", ct);
            }
            await _transport.WriteLineAsync($"NICK {_selfLogin}", ct);
            await _transport.WriteLineAsync($"JOIN #{_channel}", ct);

            _joinTimeoutTimer = _scheduler.Schedule(JoinConfirmationTimeout, () => {
                if (_state is ChatConnectionState.Connecting) {
                    TransitionTo(ChatConnectionState.JoinFailed,
                        $"JOIN confirmation timeout after {JoinConfirmationTimeout.TotalSeconds}s");
                    _cts?.Cancel();
                }
            });

            _sendQueue = new OutgoingMessageQueue(
                capacity: _sendCapacity, window: _sendWindow, minInterval: _sendMinInterval,
                clock: _clock, scheduler: _scheduler,
                send: line => _transport!.WriteLineAsync(line, ct));

            while (!ct.IsCancellationRequested) {
                var line = await _transport.ReadLineAsync(ct);
                if (line is null) break;   // remote closed
                ProcessIncomingLine(line);
            }
        } catch (OperationCanceledException) {
            // Caller-cancelled or dispose-cancelled; no state transition.
        } catch (Exception ex) {
            LastError = ex;
            TiLog.Error("[TwitchIrcChatService] read loop error", ex);
        }

        // Determine post-loop action.
        if (_disposed || _state is ChatConnectionState.Disconnected
                               or ChatConnectionState.AuthenticationFailed
                               or ChatConnectionState.JoinFailed
                               or ChatConnectionState.Disposed) {
            return;   // terminal — no reconnect
        }

        // Read returned null OR transport threw → schedule reconnect.
        ScheduleReconnect();
    }

    private void ScheduleReconnect() {
        if (_disposed || _state is ChatConnectionState.Disconnected
                                or ChatConnectionState.Disposed
                                or ChatConnectionState.AuthenticationFailed
                                or ChatConnectionState.JoinFailed) return;
        TransitionTo(ChatConnectionState.Reconnecting, "transport closed/error");
        var idx = Math.Min(_reconnectAttempt, BackoffSeconds.Length - 1);
        var nominal = BackoffSeconds[idx];
        var jitterMs = (Random.Shared.NextDouble() - 0.5) * 0.4 * nominal.TotalMilliseconds;
        var delay = nominal + TimeSpan.FromMilliseconds(jitterMs);
        _reconnectAttempt++;

        _scheduler.Schedule(delay, () => {
            if (_disposed) return;
            // Build a fresh transport + restart the read loop.
            try { _transport?.Dispose(); } catch { }
            _transport = _transportFactory();
            _cts = new CancellationTokenSource();
            TransitionTo(ChatConnectionState.Connecting, "reconnecting");
            _readLoopTask = Task.Run(() => RunConnectionAsync(_cts.Token));
        });
    }

    private void ProcessIncomingLine(string line) {
        var ev = TwitchIrcParser.Parse(line);
        if (ev is null) return;

        switch (ev) {
            case CapAckEvent: HasTags = true; break;
            case CapNakEvent:
                HasTags = false;
                TiLog.Warn("[TwitchIrcChatService] CAP NAK — falling back to no-tags mode");
                break;
            case PingEvent ping:
                _ = _transport!.WriteLineAsync($"PONG :{ping.Token}", _cts!.Token);
                break;
            case PrivmsgEvent privmsg:
                HandlePrivmsg(privmsg);
                break;
            case NoticeEvent notice:
                HandleNotice(notice);
                break;
            case ReconnectEvent:
                TiLog.Info("[TwitchIrcChatService] received RECONNECT — reconnecting");
                try { _transport?.Dispose(); } catch { }
                // The read loop will exit on null/exception; ScheduleReconnect kicks in.
                _cts?.Cancel();
                break;
            case RoomStateEvent _:
            case UserStateEvent _:
                // ROOMSTATE/USERSTATE means JOIN succeeded.
                if (_state is ChatConnectionState.Connecting) {
                    _joinTimeoutTimer?.Dispose();
                    _joinTimeoutTimer = null;
                    _reconnectAttempt = 0;
                    var nextState = _creds is null
                        ? ChatConnectionState.ConnectedReadOnly
                        : ChatConnectionState.ConnectedReadWrite;
                    TransitionTo(nextState, "JOIN confirmed");
                }
                break;
            case UnknownIrcEvent:
                // Numeric replies like 001/353/366 fall here; treat 353/366 as JOIN confirmation too.
                if (_state is ChatConnectionState.Connecting && Is366Or353(line)) {
                    _joinTimeoutTimer?.Dispose();
                    _joinTimeoutTimer = null;
                    _reconnectAttempt = 0;
                    var nextState = _creds is null
                        ? ChatConnectionState.ConnectedReadOnly
                        : ChatConnectionState.ConnectedReadWrite;
                    TransitionTo(nextState, "JOIN confirmed via numeric");
                }
                break;
        }
    }

    private static bool Is366Or353(string line) =>
        line.Contains(" 353 ", StringComparison.Ordinal) || line.Contains(" 366 ", StringComparison.Ordinal);

    private void HandlePrivmsg(PrivmsgEvent privmsg) {
        LastMessageReceivedAt = _clock.UtcNow;
        if (IsSelfEcho(privmsg.Message)) return;
        var msg = privmsg.Message;
        _dispatcher.Post(() => MessageReceived?.Invoke(this, msg));
    }

    private bool IsSelfEcho(ChatMessage msg) {
        if (_selfLogin is null) return false;
        return string.Equals(msg.Login, _selfLogin, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleNotice(NoticeEvent notice) {
        var msgId = notice.MsgId?.ToLowerInvariant();
        var text = notice.Text;

        // Terminal: auth failure (matches by msg-id when tags enabled, by text otherwise).
        bool isAuthFailure =
            msgId is "msg_login_unsuccessful" or "msg_authentication_failed" or "improperly_formatted_auth" ||
            text.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Error logging in", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase);
        if (isAuthFailure) {
            TransitionTo(ChatConnectionState.AuthenticationFailed, $"NOTICE: {text}");
            _cts?.Cancel();   // stop the read loop; no retry
            return;
        }

        // Terminal: channel banned/suspended.
        bool isJoinFailure =
            msgId is "msg_banned" or "msg_channel_suspended" or "tos_ban";
        if (isJoinFailure) {
            TransitionTo(ChatConnectionState.JoinFailed, $"NOTICE: {text}");
            _cts?.Cancel();
            return;
        }

        bool isRateLimit = msgId is "msg_ratelimit" or "msg_slowmode";
        if (isRateLimit) {
            TiLog.Warn($"[TwitchIrcChatService] Twitch ratelimit/slowmode NOTICE: {text}");
            return;
        }
        bool isDuplicate = msgId is "msg_duplicate";
        if (isDuplicate) {
            TiLog.Debug($"[TwitchIrcChatService] duplicate-message NOTICE dropped: {text}");
            return;
        }
    }

    private void TransitionTo(ChatConnectionState next, string? reason = null) {
        if (_state == next) return;
        var old = _state;
        _state = next;
        var args = new ChatConnectionChangedEventArgs(old, next, reason);
        _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args));
    }

    private static string NormaliseChannel(string raw) {
        var lower = raw.Trim().ToLowerInvariant();
        return lower.StartsWith("#") ? lower.Substring(1) : lower;
    }

    public void Disconnect() {
        if (_state is ChatConnectionState.Disposed) return;
        _cts?.Cancel();
        try { _transport?.Dispose(); } catch { }
        _transport = null;
        TransitionTo(ChatConnectionState.Disconnected, "Disconnect called");
    }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(TwitchIrcChatService)));
        if (!CanSend || _sendQueue is null || _channel is null) {
            return Task.FromException(new InvalidOperationException($"Cannot send in state {_state}"));
        }
        var line = $"PRIVMSG #{_channel} :{text}";
        _sendQueue.Enqueue(line, priority);
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _transport?.Dispose(); } catch { }
        try { _sendQueue?.Dispose(); } catch { }
        try { _joinTimeoutTimer?.Dispose(); } catch { }
        _transport = null;
        _sendQueue = null;
        var old = _state;
        _state = ChatConnectionState.Disposed;
        var args = new ChatConnectionChangedEventArgs(old, ChatConnectionState.Disposed, "Dispose");
        try { _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args)); } catch { }
    }
}

// Placeholder so the production constructor compiles. Real impl in Task 30.
internal sealed class SslIrcTransport : IIrcTransport {
    public Task ConnectAsync(string host, int port, CancellationToken ct) =>
        throw new NotImplementedException("SslIrcTransport implemented in Task 30");
    public Task<string?> ReadLineAsync(CancellationToken ct) =>
        throw new NotImplementedException();
    public Task WriteLineAsync(string line, CancellationToken ct) =>
        throw new NotImplementedException();
    public void Dispose() { }
}

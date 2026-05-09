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

    private const string TwitchIrcHost = "irc.chat.twitch.tv";
    private const int TwitchIrcPort = 6697;

    private IIrcTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;
    private string? _selfLogin;
    private string? _channel;
    private ChatCredentials? _creds;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _state is ChatConnectionState.ConnectedReadWrite;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

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

            while (!ct.IsCancellationRequested) {
                var line = await _transport.ReadLineAsync(ct);
                if (line is null) break;   // remote closed
                ProcessIncomingLine(line);
            }
        } catch (OperationCanceledException) {
            // Caller-cancelled or dispose-cancelled; no state transition needed.
            // The disposer (or Disconnect, in Task 27) is responsible for any state change.
        } catch (Exception ex) {
            LastError = ex;
            TiLog.Error("[TwitchIrcChatService] read loop error", ex);
            if (!_disposed) TransitionTo(ChatConnectionState.Disconnected, "transport error");
        }
    }

    private void ProcessIncomingLine(string line) {
        var ev = TwitchIrcParser.Parse(line);
        if (ev is null) return;

        switch (ev) {
            case CapAckEvent: /* tags + commands acknowledged */ break;
            case CapNakEvent: /* TODO Task 16: fall back to no-tags mode */ break;
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
                // TODO Task 29: graceful disconnect + reconnect
                break;
            case RoomStateEvent _:
            case UserStateEvent _:
                // ROOMSTATE/USERSTATE means JOIN succeeded.
                if (_state is ChatConnectionState.Connecting) {
                    TransitionTo(ChatConnectionState.ConnectedReadWrite, "JOIN confirmed");
                }
                break;
            case UnknownIrcEvent:
                // Numeric replies like 001/353/366 fall here; treat 353/366 as JOIN confirmation too.
                if (_state is ChatConnectionState.Connecting && Is366Or353(line)) {
                    TransitionTo(ChatConnectionState.ConnectedReadWrite, "JOIN confirmed via numeric");
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
        // TODO Task 15/23: handle terminal notices + rate-limit notices
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
        // Stub.
    }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        // Stub.
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _transport?.Dispose();
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

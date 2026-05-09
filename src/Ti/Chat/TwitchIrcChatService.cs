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
        // Stub — real impl in subsequent tasks.
        return Task.CompletedTask;
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

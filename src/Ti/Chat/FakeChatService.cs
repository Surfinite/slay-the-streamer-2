using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// In-memory IChatService for tests and dev. Inject() delivers messages
/// synchronously to subscribers; SentMessages records every outgoing send.
/// </summary>
public sealed class FakeChatService : IChatService {
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private readonly List<SentMessage> _sent = new();

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _state == ChatConnectionState.ConnectedReadWrite;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Outgoing messages recorded for assertions.</summary>
    public IReadOnlyList<SentMessage> SentMessages => _sent.AsReadOnly();

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        SetState(creds is null ? ChatConnectionState.ConnectedReadOnly : ChatConnectionState.ConnectedReadWrite);
        return Task.CompletedTask;
    }

    public void Disconnect() => SetState(ChatConnectionState.Disconnected);

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        if (!CanSend)
            return Task.FromException(new InvalidOperationException(
                $"Cannot send while State = {_state} (CanSend == false)"));
        _sent.Add(new SentMessage(text, priority));
        return Task.CompletedTask;
    }

    /// <summary>Deliver a message synchronously to MessageReceived subscribers.</summary>
    public void Inject(ChatMessage message) {
        LastMessageReceivedAt = message.ReceivedAt;
        MessageReceived?.Invoke(this, message);
    }

    /// <summary>Force the service into a specific state (e.g. simulate auth failure or mid-vote disconnect).</summary>
    public void SimulateState(ChatConnectionState state, string? reason = null) => SetState(state, reason);

    public void Dispose() => SetState(ChatConnectionState.Disposed);

    private void SetState(ChatConnectionState next, string? reason = null) {
        if (_state == next) return;
        var old = _state;
        _state = next;
        ConnectionStateChanged?.Invoke(this, new ChatConnectionChangedEventArgs(old, next, reason));
    }

    public sealed record SentMessage(string Text, OutgoingMessagePriority Priority);
}

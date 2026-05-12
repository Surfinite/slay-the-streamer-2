using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool? _canSendOverride;
    private bool _sendThrows;
    private bool _disposeThrows;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _canSendOverride ?? (_state == ChatConnectionState.ConnectedReadWrite);
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Outgoing messages recorded for assertions.</summary>
    public IReadOnlyList<SentMessage> SentMessages => _sent.AsReadOnly();

    /// <summary>Outgoing message text only — convenience for MultiChatService tests.</summary>
    public IReadOnlyList<string> Sent => _sent.Select(m => m.Text).ToList();

    /// <summary>True once Disconnect() has been called at least once.</summary>
    public bool DisconnectCalled { get; private set; }

    /// <summary>True once Dispose() has been called at least once.</summary>
    public bool DisposeCalled { get; private set; }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        SetState(creds is null ? ChatConnectionState.ConnectedReadOnly : ChatConnectionState.ConnectedReadWrite);
        return Task.CompletedTask;
    }

    public void Disconnect() {
        DisconnectCalled = true;
        SetState(ChatConnectionState.Disconnected);
    }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        if (_sendThrows)
            return Task.FromException(new InvalidOperationException("SimulateSendThrow"));
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

    /// <summary>Alias of Inject for naming parity with other test helpers.</summary>
    public void SimulateIncoming(ChatMessage message) => Inject(message);

    /// <summary>Force the service into a specific state (e.g. simulate auth failure or mid-vote disconnect).</summary>
    public void SimulateState(ChatConnectionState state, string? reason = null) => SetState(state, reason);

    /// <summary>Override CanSend independent of state (test-only).</summary>
    public void SetCanSend(bool canSend) => _canSendOverride = canSend;

    /// <summary>Make every subsequent SendMessageAsync return a faulted task.</summary>
    public void SimulateSendThrow() => _sendThrows = true;

    /// <summary>Make Dispose() throw instead of transitioning state.</summary>
    public void SimulateDisposeThrow() => _disposeThrows = true;

    public void Dispose() {
        DisposeCalled = true;
        if (_disposeThrows) throw new InvalidOperationException("SimulateDisposeThrow");
        SetState(ChatConnectionState.Disposed);
    }

    private void SetState(ChatConnectionState next, string? reason = null) {
        if (_state == next) return;
        var old = _state;
        _state = next;
        ConnectionStateChanged?.Invoke(this, new ChatConnectionChangedEventArgs(old, next, reason));
    }

    public sealed record SentMessage(string Text, OutgoingMessagePriority Priority);
}

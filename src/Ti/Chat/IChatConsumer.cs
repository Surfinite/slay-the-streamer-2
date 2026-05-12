using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Read/send/state surface of a chat service. Does NOT include connect-lifecycle.
/// Aggregators (MultiChatService) implement this without exposing ConnectAsync,
/// since children must be connected by the wiring code before construction.
/// </summary>
public interface IChatConsumer : IDisposable {
    ChatConnectionState State { get; }
    bool IsConnected { get; }
    bool CanSend { get; }
    DateTimeOffset? LastMessageReceivedAt { get; }
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    void Disconnect();
    Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default);
}

using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Twitch chat I/O. The real impl is TwitchIrcChatService (Plan B); Plan A
/// uses FakeChatService for all tests.
/// </summary>
public interface IChatService : IDisposable {
    ChatConnectionState State { get; }
    bool IsConnected { get; }              // convenience: any of ConnectedReadOnly/ConnectedReadWrite/Reconnecting
    bool CanSend { get; }                  // false in Anonymous/Disconnected/AuthFailed/JoinFailed/Disposed
    DateTimeOffset? LastMessageReceivedAt { get; }
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
    void Disconnect();

    Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default);
}

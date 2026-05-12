using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// IChatConsumer + connect lifecycle. Twitch and YouTube both implement this.
/// MultiChatService implements only IChatConsumer (no ConnectAsync — children
/// are pre-connected by ModEntry).
/// </summary>
public interface IChatService : IChatConsumer {
    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
}

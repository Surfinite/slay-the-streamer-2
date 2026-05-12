using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal interface IYouTubeLiveBroadcastDiscovery {
    /// <summary>
    /// Returns the live videoId if the channel has an active broadcast; null otherwise.
    /// All exceptions are caught internally — return value is the sole signal.
    /// </summary>
    Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct);
}

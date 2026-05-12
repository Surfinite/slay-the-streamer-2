using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal interface IYouTubeLiveChatScraper {
    Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct);
    Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct);
}

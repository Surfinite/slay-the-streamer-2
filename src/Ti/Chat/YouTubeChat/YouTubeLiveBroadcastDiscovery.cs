using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeLiveBroadcastDiscovery : IYouTubeLiveBroadcastDiscovery {
    private readonly IYouTubeHttp _http;

    public YouTubeLiveBroadcastDiscovery(IYouTubeHttp http) {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(channelId)) return null;
        try {
            var url = new Uri($"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}/live");
            using var resp = await _http.GetWithRedirectAsync(url, ct).ConfigureAwait(false);
            var finalUri = resp.RequestMessage?.RequestUri;
            if (finalUri is null) return null;
            if (!string.Equals(finalUri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(finalUri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase)) return null;
            var videoId = GetQueryParam(finalUri.Query, "v");
            return string.IsNullOrEmpty(videoId) ? null : videoId;
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveBroadcastDiscovery] FindLiveVideoIdAsync threw for {channelId}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Manual query-string parsing to avoid System.Web dependency (deprecated namespace);
    // handles ?v=X, ?foo=bar&v=X, and ?v=X&foo=bar variants per spec D4 tests.
    private static string? GetQueryParam(string query, string key) {
        if (string.IsNullOrEmpty(query)) return null;
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&')) {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (string.Equals(pair[..eq], key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}

using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeLiveBroadcastDiscovery : IYouTubeLiveBroadcastDiscovery {
    // YouTube no longer redirects /channel/{ID}/live → /watch?v=... (verified 2026-05-12).
    // The live page is served at the original URL with the live videoId embedded
    // in <link rel="canonical" href="https://www.youtube.com/watch?v=VIDEOID">.
    // If no live broadcast is active, the canonical link points elsewhere (typically
    // the channel page itself), so the absence of a /watch?v= canonical = no live.
    private static readonly Regex CanonicalWatchRegex = new(
        @"<link\s+rel=""canonical""\s+href=""https://www\.youtube\.com/watch\?v=([A-Za-z0-9_-]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IYouTubeHttp _http;

    public YouTubeLiveBroadcastDiscovery(IYouTubeHttp http) {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(channelId)) return null;
        try {
            var url = new Uri($"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}/live");
            using var resp = await _http.GetWithRedirectAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var match = CanonicalWatchRegex.Match(body);
            if (!match.Success) {
                // Diagnostic: log enough to identify what page YouTube returned (consent wall,
                // stripped mobile, real-but-no-broadcast, etc.). Truncated to keep log volume sane.
                var sample = body.Length > 1500 ? body.Substring(0, 1500) : body;
                var hasConsent = body.Contains("consent.youtube.com", StringComparison.OrdinalIgnoreCase);
                var hasCanonical = body.Contains("canonical", StringComparison.OrdinalIgnoreCase);
                var hasOgUrl = body.Contains("og:url", StringComparison.OrdinalIgnoreCase);
                var hasWatchUrl = body.Contains("youtube.com/watch?v=", StringComparison.OrdinalIgnoreCase);
                TiLog.Info($"[YouTubeLiveBroadcastDiscovery] no canonical match for {channelId}: " +
                    $"body length={body.Length}, hasConsent={hasConsent}, hasCanonical={hasCanonical}, " +
                    $"hasOgUrl={hasOgUrl}, hasWatchUrl={hasWatchUrl}");
                TiLog.Info($"[YouTubeLiveBroadcastDiscovery] body[0..1500]={sample}");
                return null;
            }
            return match.Groups[1].Value;
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveBroadcastDiscovery] FindLiveVideoIdAsync threw for {channelId}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

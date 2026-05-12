using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeLiveChatScraper : IYouTubeLiveChatScraper {
    private const string ScraperRevision = "yt-scraper-2026-05-12-a";

    // Regex notes (per spike findings in notes/06-followups-and-deferred.md):
    // - ApiKey: simple match against the documented constant.
    // - ClientVersion: locate INNERTUBE_CONTEXT, then the client sub-object's clientVersion.
    // - Continuation: lazy multiline match. The Task 1 spike documented a brittleness
    //   in the previous `[^}]*` form (it bails at the first nested `}`, so the fixture
    //   had to be hand-crafted to keep `continuation` as the first field). The lazy
    //   `[\s\S]*?` here matches across newlines AND handles nested objects between
    //   the renderer-data start and the continuation field — robust to YouTube
    //   reordering or inserting nested keys before `continuation`.
    private static readonly Regex ApiKeyRegex = new(
        @"""INNERTUBE_API_KEY""\s*:\s*""([A-Za-z0-9_-]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ClientVersionRegex = new(
        @"""INNERTUBE_CONTEXT""[\s\S]*?""client""\s*:\s*\{[\s\S]*?""clientVersion""\s*:\s*""([0-9.]+)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ContinuationRegex = new(
        @"""(?:invalidationContinuationData|timedContinuationData)""\s*:\s*\{[\s\S]*?""continuation""\s*:\s*""([A-Za-z0-9_=-]+)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly IYouTubeHttp _http;
    private bool _firstSuccessLogged;

    // Single-location health-check telemetry per Round-2 C-10 + C-20 (PII-safe).
    private string? _lastFailureLocation;
    private int _consecutiveFailureCount;

    public YouTubeLiveChatScraper(IYouTubeHttp http) {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct) {
        try {
            var url = new Uri($"https://www.youtube.com/live_chat?v={Uri.EscapeDataString(videoId)}");
            using var resp = await _http.GetWithRedirectAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var apiKeyMatch = ApiKeyRegex.Match(body);
            if (!apiKeyMatch.Success) { RecordFailure("INNERTUBE_API_KEY_regex", body); return null; }

            var versionMatch = ClientVersionRegex.Match(body);
            if (!versionMatch.Success) { RecordFailure("INNERTUBE_CONTEXT_clientVersion", body); return null; }

            var continuationMatch = ContinuationRegex.Match(body);
            if (!continuationMatch.Success) { RecordFailure("initial_continuation_extract", body); return null; }

            RecordSuccess(videoId);
            return new InitialPageParseResult(
                InnertubeApiKey: apiKeyMatch.Groups[1].Value,
                ClientVersion: versionMatch.Groups[1].Value,
                InitialContinuation: continuationMatch.Groups[1].Value);
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveChatScraper] ParseInitialPageAsync threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) =>
        throw new NotImplementedException("Implemented in Task 18");

    private void RecordSuccess(string videoId) {
        _lastFailureLocation = null;
        _consecutiveFailureCount = 0;
        if (!_firstSuccessLogged) {
            TiLog.Info($"[YouTubeLiveChatScraper] scraper {ScraperRevision} active; tracking videoId={videoId}");
            _firstSuccessLogged = true;
        }
    }

    private void RecordFailure(string location, string responseBody) {
        if (_lastFailureLocation == location) {
            _consecutiveFailureCount++;
        } else {
            _lastFailureLocation = location;
            _consecutiveFailureCount = 1;
        }
        if (_consecutiveFailureCount == 5) {
            var structuralSample = BuildStructuralSample(responseBody);
            TiLog.Error($"[YouTubeLiveChatScraper] 5 consecutive parse failures at {location}; structural sample: {structuralSample}", null);
        }
    }

    private static string BuildStructuralSample(string body) {
        var len = body.Length;
        var topKeys = Regex.Matches(body, @"""([A-Za-z]+)""\s*:", RegexOptions.None)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(k => k.EndsWith("Renderer") || k == "responseContext" || k == "contents" || k == "continuationContents")
            .Take(10);
        return $"length={len}, observed keys = [{string.Join(", ", topKeys)}]";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    public async Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) {
        try {
            var url = new Uri($"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={Uri.EscapeDataString(apiKey)}");
            var body = BuildPollRequestBody(clientVersion, continuation);
            using var resp = await _http.PostJsonAsync(url, body, ct).ConfigureAwait(false);
            var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // continuationContents.liveChatContinuation is the wrapper for both actions[] and continuations[].
            // Missing either node means the broadcast ended (or members-only-like; see fixture).
            if (!root.TryGetProperty("continuationContents", out var contents) ||
                !contents.TryGetProperty("liveChatContinuation", out var liveChatContinuation)) {
                RecordFailure("continuationContents.liveChatContinuation_missing", responseBody);
                return new PollResult(Array.Empty<ParsedChatMessage>(), null, 0);
            }

            var messages = new List<ParsedChatMessage>();
            if (liveChatContinuation.TryGetProperty("actions", out var actions) &&
                actions.ValueKind == JsonValueKind.Array) {
                foreach (var action in actions.EnumerateArray()) {
                    var parsed = TryParseAction(action);
                    if (parsed is not null) messages.Add(parsed);
                }
            }

            var (nextContinuation, nextTimeoutMs) = ExtractContinuation(liveChatContinuation);

            RecordSuccess("(poll)");
            return new PollResult(messages, nextContinuation, nextTimeoutMs);
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveChatScraper] PollAsync threw: {ex.GetType().Name}: {ex.Message}");
            return new PollResult(Array.Empty<ParsedChatMessage>(), null, 0);
        }
    }

    private static string BuildPollRequestBody(string clientVersion, string continuation) {
        // Minimal Innertube WEB context — matches what every cross-language scraper sends.
        // Built with JsonSerializer (not string concat) so embedded quotes in clientVersion
        // (unlikely but possible after a YouTube redesign) don't break the JSON.
        var payload = new {
            context = new {
                client = new {
                    clientName = "WEB",
                    clientVersion = clientVersion,
                },
            },
            continuation = continuation,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static ParsedChatMessage? TryParseAction(JsonElement action) {
        // Path: action.addChatItemAction.item.{liveChatTextMessageRenderer | liveChatPaidMessageRenderer}
        // Defensive at every step; any missing node returns null silently.
        if (!action.TryGetProperty("addChatItemAction", out var addAction)) return null;
        if (!addAction.TryGetProperty("item", out var item)) return null;

        JsonElement renderer;
        if (item.TryGetProperty("liveChatTextMessageRenderer", out var textRenderer)) {
            renderer = textRenderer;
        } else if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paidRenderer)) {
            // Paid messages WITH a message.runs body are extracted as normal text per spec.
            // Sticker-only / text-less paid items fall out below when runs[] yields empty text.
            renderer = paidRenderer;
        } else {
            // membership / sticker / engagement / other — skip silently.
            return null;
        }

        // Author channel id is required (no anon-GUID fallback per Should-do #12).
        if (!renderer.TryGetProperty("authorExternalChannelId", out var channelIdEl) ||
            channelIdEl.ValueKind != JsonValueKind.String) {
            TiLog.Debug("[YouTubeLiveChatScraper] dropped message: missing authorExternalChannelId");
            return null;
        }
        var channelId = channelIdEl.GetString();
        if (string.IsNullOrEmpty(channelId)) {
            TiLog.Debug("[YouTubeLiveChatScraper] dropped message: empty authorExternalChannelId");
            return null;
        }

        // Walk message.runs[]; concat any run that has a text field. Skip emoji/image-only runs.
        if (!renderer.TryGetProperty("message", out var message)) {
            TiLog.Debug("[YouTubeLiveChatScraper] dropped message: missing message field");
            return null;
        }
        if (!message.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) {
            TiLog.Debug("[YouTubeLiveChatScraper] dropped message: missing runs[]");
            return null;
        }

        var sb = new StringBuilder();
        foreach (var run in runs.EnumerateArray()) {
            if (run.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String) {
                sb.Append(textEl.GetString());
            }
            // emoji/image runs without a text field: skipped silently per Should-do #28.
        }
        var text = sb.ToString();
        if (string.IsNullOrEmpty(text)) {
            // No text content at all (e.g. sticker-only paid item) — skip silently.
            return null;
        }

        // Author display name from authorName.simpleText; fall back to empty if missing.
        string displayName = string.Empty;
        if (renderer.TryGetProperty("authorName", out var authorName) &&
            authorName.TryGetProperty("simpleText", out var simpleText) &&
            simpleText.ValueKind == JsonValueKind.String) {
            displayName = simpleText.GetString() ?? string.Empty;
        }

        // Walk authorBadges[] for member (customThumbnails) and moderator (icon.iconType == "MODERATOR").
        bool isMember = false, isModerator = false;
        if (renderer.TryGetProperty("authorBadges", out var badges) && badges.ValueKind == JsonValueKind.Array) {
            foreach (var badge in badges.EnumerateArray()) {
                if (!badge.TryGetProperty("liveChatAuthorBadgeRenderer", out var br)) continue;
                if (br.TryGetProperty("customThumbnails", out _)) isMember = true;
                if (br.TryGetProperty("icon", out var icon) &&
                    icon.TryGetProperty("iconType", out var iconType) &&
                    iconType.ValueKind == JsonValueKind.String &&
                    string.Equals(iconType.GetString(), "MODERATOR", StringComparison.Ordinal)) {
                    isModerator = true;
                }
            }
        }

        return new ParsedChatMessage(
            AuthorChannelId: channelId!,
            AuthorDisplayName: displayName,
            Text: text,
            IsChatMember: isMember,
            IsChatModerator: isModerator);
    }

    private static (string? Continuation, int TimeoutMs) ExtractContinuation(JsonElement liveChatContinuation) {
        if (!liveChatContinuation.TryGetProperty("continuations", out var continuations) ||
            continuations.ValueKind != JsonValueKind.Array ||
            continuations.GetArrayLength() == 0) {
            return (null, 0);
        }
        var first = continuations[0];
        JsonElement data;
        if (first.TryGetProperty("invalidationContinuationData", out var invalidation)) {
            data = invalidation;
        } else if (first.TryGetProperty("timedContinuationData", out var timed)) {
            data = timed;
        } else {
            return (null, 0);
        }
        string? token = null;
        if (data.TryGetProperty("continuation", out var contEl) && contEl.ValueKind == JsonValueKind.String) {
            token = contEl.GetString();
        }
        int timeoutMs = 0;
        if (data.TryGetProperty("timeoutMs", out var timeoutEl)) {
            if (timeoutEl.ValueKind == JsonValueKind.Number && timeoutEl.TryGetInt32(out var t)) {
                timeoutMs = t;
            } else if (timeoutEl.ValueKind == JsonValueKind.String && int.TryParse(timeoutEl.GetString(), out var ts)) {
                timeoutMs = ts;
            }
        }
        return (token, timeoutMs);
    }

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

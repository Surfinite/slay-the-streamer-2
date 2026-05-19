using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SlayTheStreamer2.Ti.Chat;

namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(
    string Channel,
    ChatCredentials Credentials,
    int CardSkipsPerAct,
    string? YoutubeChannelId,
    bool VoteOnActVariant = true,
    bool ForceL3PopupFallback = false,
    int VoteDurationSeconds = 30,
    bool CardSkipAsVoteOption = true);

public abstract record SettingsResult {
    public sealed record Success(ChatSettings Settings, IReadOnlyList<string> Warnings) : SettingsResult;
    public sealed record Missing(string Path) : SettingsResult;
    public sealed record Malformed(string Path, string Reason) : SettingsResult;

    private SettingsResult() { }   // restrict subclassing to nested records
}

public static class ModSettings {
    public const int CurrentSchemaVersion = 1;

    public static SettingsResult Load(string path) {
        if (!File.Exists(path)) return new SettingsResult.Missing(path);

        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { return new SettingsResult.Malformed(path, $"failed to read file: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(raw)) return new SettingsResult.Malformed(path, "file is empty");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException ex) { return new SettingsResult.Malformed(path, $"JSON parse error: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;

            if (!root.TryGetProperty("schemaVersion", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number) {
                return new SettingsResult.Malformed(path, "schemaVersion is missing or not a number");
            }
            var version = versionProp.GetInt32();
            if (version != CurrentSchemaVersion) {
                return new SettingsResult.Malformed(path,
                    $"unknown schemaVersion {version}; this mod build supports schemaVersion {CurrentSchemaVersion}");
            }

            var warnings = new List<string>();

            var channel = ReadStringOrNull(root, "channel");
            var username = ReadStringOrNull(root, "username");
            var oauthToken = ReadStringOrNull(root, "oauthToken");

            if (string.IsNullOrWhiteSpace(channel)) return new SettingsResult.Malformed(path, "channel is missing or empty");
            if (string.IsNullOrWhiteSpace(username)) return new SettingsResult.Malformed(path, "username is missing or empty");
            if (string.IsNullOrWhiteSpace(oauthToken)) return new SettingsResult.Malformed(path, "oauthToken is missing or empty");

            var (normalisedChannel, channelWarning) = NormaliseChannel(channel);
            if (channelWarning is not null) warnings.Add(channelWarning);

            if (!string.Equals(username, username.ToLowerInvariant(), StringComparison.Ordinal)) {
                warnings.Add($"username '{username}' lowercased to '{username.ToLowerInvariant()}'");
            }

            // Strip optional oauth: prefix for shape inspection.
            var bareForCheck = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
                ? oauthToken.Substring(6) : oauthToken;
            if (bareForCheck.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) {
                return new SettingsResult.Malformed(path, "oauthToken contains whitespace or control characters");
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(bareForCheck, "^[a-z0-9]{30}$")) {
                warnings.Add(
                    "oauth token doesn't match the common Twitch user-access-token shape " +
                    "(30 lowercase alphanumeric chars); will let Twitch authentication be the source of truth");
            }

            int cardSkipsPerAct = 1;   // default
            if (root.TryGetProperty("cardSkipsPerAct", out var skipsProp)) {
                if (skipsProp.ValueKind != JsonValueKind.Number || !skipsProp.TryGetInt32(out var rawSkips)) {
                    warnings.Add("cardSkipsPerAct is not an integer; using default (1)");
                } else if (rawSkips < -1) {
                    warnings.Add($"cardSkipsPerAct {rawSkips} clamped to -1 (unlimited)");
                    cardSkipsPerAct = -1;
                } else {
                    cardSkipsPerAct = rawSkips;
                }
            }

            // youtubeChannelId (optional, D6 v4 trim-first). Malformed values disable YT only,
            // not the whole mod — Twitch continues. Empty / whitespace-only → silent null.
            string? youtubeChannelId = null;
            if (root.TryGetProperty("youtubeChannelId", out var ytEl) && ytEl.ValueKind != JsonValueKind.Null) {
                if (ytEl.ValueKind != JsonValueKind.String) {
                    warnings.Add("youtubeChannelId must be a string; YouTube integration disabled.");
                } else {
                    var ytRaw = ytEl.GetString() ?? "";
                    var trimmed = ytRaw.Trim();
                    if (trimmed.Length == 0) {
                        // Empty / whitespace-only → clamp to null silently (common paste artifact).
                        youtubeChannelId = null;
                    } else if (trimmed.Any(char.IsControl)) {
                        warnings.Add("youtubeChannelId malformed (control characters); YouTube integration disabled.");
                        youtubeChannelId = null;
                    } else {
                        youtubeChannelId = trimmed;
                    }
                }
            }

            bool voteOnActVariant = true;
            if (root.TryGetProperty("voteOnActVariant", out var voteActProp)) {
                if (voteActProp.ValueKind == JsonValueKind.True) voteOnActVariant = true;
                else if (voteActProp.ValueKind == JsonValueKind.False) voteOnActVariant = false;
                else warnings.Add("voteOnActVariant is not a boolean; using default (true)");
            }

            bool forceL3PopupFallback = false;
            if (root.TryGetProperty("forceL3PopupFallback", out var forceL3Prop)) {
                if (forceL3Prop.ValueKind == JsonValueKind.True) forceL3PopupFallback = true;
                else if (forceL3Prop.ValueKind == JsonValueKind.False) forceL3PopupFallback = false;
                else warnings.Add("forceL3PopupFallback is not a boolean; using default (false)");
            }

            int voteDurationSeconds = 30;
            if (root.TryGetProperty("voteDurationSeconds", out var voteDurProp)) {
                if (voteDurProp.ValueKind != JsonValueKind.Number || !voteDurProp.TryGetInt32(out var rawDur)) {
                    warnings.Add("voteDurationSeconds is not an integer; using default (30)");
                } else if (rawDur < 10) {
                    warnings.Add($"voteDurationSeconds {rawDur} below minimum; clamped to 10");
                    voteDurationSeconds = 10;
                } else if (rawDur > 120) {
                    warnings.Add($"voteDurationSeconds {rawDur} above maximum; clamped to 120");
                    voteDurationSeconds = 120;
                } else {
                    voteDurationSeconds = rawDur;
                }
            }

            bool cardSkipAsVoteOption = true;
            if (root.TryGetProperty("cardSkipAsVoteOption", out var skipAsVoteProp)) {
                if (skipAsVoteProp.ValueKind == JsonValueKind.True) cardSkipAsVoteOption = true;
                else if (skipAsVoteProp.ValueKind == JsonValueKind.False) cardSkipAsVoteOption = false;
                else warnings.Add("cardSkipAsVoteOption is not a boolean; using default (true)");
            }

            var creds = new ChatCredentials(username, oauthToken);
            return new SettingsResult.Success(
                new ChatSettings(normalisedChannel, creds, cardSkipsPerAct, youtubeChannelId, voteOnActVariant, forceL3PopupFallback, voteDurationSeconds, cardSkipAsVoteOption),
                warnings);
        }
    }

    private static string? ReadStringOrNull(JsonElement root, string name) {
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static (string Normalised, string? Warning) NormaliseChannel(string raw) {
        var trimmed = raw.Trim();
        var lower = trimmed.ToLowerInvariant();

        string normalised;
        if (lower.StartsWith("https://www.twitch.tv/", StringComparison.Ordinal)) {
            normalised = lower.Substring("https://www.twitch.tv/".Length);
        } else if (lower.StartsWith("http://www.twitch.tv/", StringComparison.Ordinal)) {
            normalised = lower.Substring("http://www.twitch.tv/".Length);
        } else if (lower.StartsWith("https://twitch.tv/", StringComparison.Ordinal)) {
            normalised = lower.Substring("https://twitch.tv/".Length);
        } else if (lower.StartsWith("http://twitch.tv/", StringComparison.Ordinal)) {
            normalised = lower.Substring("http://twitch.tv/".Length);
        } else if (lower.StartsWith("#", StringComparison.Ordinal)) {
            normalised = lower.Substring(1);
        } else {
            normalised = lower;
        }
        // Strip trailing slash and any trailing path segment.
        var slashIdx = normalised.IndexOf('/');
        if (slashIdx >= 0) normalised = normalised.Substring(0, slashIdx);

        string? warning = (normalised != trimmed)
            ? $"channel '{trimmed}' normalised to '{normalised}'"
            : null;
        return (normalised, warning);
    }
}

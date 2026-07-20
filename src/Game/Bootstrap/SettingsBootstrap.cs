using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlayTheStreamer2.Game.Bootstrap;

/// <summary>
/// First-boot settings-file bootstrap. Runs in ModEntry.Init BEFORE
/// ModSettings.Load:
///   - no file → write the template (same shape as slay_the_streamer_2.json.example)
///     so the user fills in a file that already exists at the right path instead
///     of creating one by hand (FrostPrime onboarding pain, 2026-06-08);
///   - file exists → additive key migration: add any known key that is missing,
///     NEVER overwriting an existing value and preserving unknown keys, so new
///     settings become discoverable in existing installs without clobbering edits.
///
/// A file that doesn't parse, isn't a JSON object, or carries a different
/// schemaVersion is left byte-for-byte untouched — the user may be mid-edit,
/// or the file may belong to a newer mod build; ModSettings.Load surfaces the
/// problem on its own terms.
/// </summary>
public static class SettingsBootstrap {
    // Placeholder values shared with slay_the_streamer_2.json.example.
    // ModSettings.Load treats a file where all three credential fields are
    // empty-or-placeholder as SettingsResult.Unconfigured (friendly log), and
    // a partially-replaced placeholder as Malformed (points at the field).
    public const string PlaceholderChannel    = "your_twitch_channel";
    public const string PlaceholderUsername   = "your_twitch_bot_username";
    public const string PlaceholderOauthToken = "oauth:your_30_character_lowercase_alphanumeric_token";

    public abstract record Outcome {
        public sealed record CreatedTemplate(string Path) : Outcome;
        public sealed record AddedMissingKeys(string Path, IReadOnlyList<string> Keys) : Outcome;
        public sealed record NoChange : Outcome;
        public sealed record SkippedUnparseable(string Reason) : Outcome;
        public sealed record Failed(string Reason) : Outcome;
        private Outcome() { }
    }

    /// <summary>
    /// Known keys with their template defaults, in display order. Must stay in
    /// step with ModSettings.Load's defaults and the .json.example file.
    /// </summary>
    private static JsonObject BuildTemplate() => new() {
        ["schemaVersion"]        = ModSettings.CurrentSchemaVersion,
        ["channel"]              = PlaceholderChannel,
        ["username"]             = PlaceholderUsername,
        ["oauthToken"]           = PlaceholderOauthToken,
        ["youtubeChannelId"]     = null,
        ["cardSkipsPerAct"]      = 1,
        ["voteOnActVariant"]     = true,
        ["voteDurationSeconds"]  = 30,
        ["cardSkipAsVoteOption"] = true,
        ["showVoteTag"]          = false,
        ["voteTallyOnLeft"]      = false,
        ["allowSameBossTwice"]   = false,
        ["relicChoices"]         = 1,
    };

    /// <summary>
    /// Keys the additive migration must NOT add to an existing file.
    /// showVoteTag's runtime default is conditional (true when youtubeChannelId
    /// is configured, else false) — writing a static false into a YT-configured
    /// file that omits the key would silently flip its effective value.
    /// </summary>
    private static readonly HashSet<string> MergeSkipKeys = new() { "showVoteTag" };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string BuildTemplateJson() => BuildTemplate().ToJsonString(Indented);

    /// <summary>
    /// Add template keys missing from <paramref name="json"/> (except
    /// <see cref="MergeSkipKeys"/>). Existing values — including explicit nulls —
    /// are never touched. Returns the added key names in template order.
    /// </summary>
    internal static IReadOnlyList<string> AddMissingKeys(JsonObject json) {
        var added = new List<string>();
        foreach (var (key, defaultValue) in BuildTemplate()) {
            if (MergeSkipKeys.Contains(key)) continue;
            if (json.ContainsKey(key)) continue;
            json[key] = defaultValue?.DeepClone();
            added.Add(key);
        }
        return added;
    }

    public static Outcome EnsureFile(string path) {
        try {
            if (!File.Exists(path)) {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                WriteAtomic(path, BuildTemplateJson(), backupExisting: false);
                return new Outcome.CreatedTemplate(path);
            }

            var raw = File.ReadAllText(path);
            JsonObject? json;
            try { json = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException ex) {
                return new Outcome.SkippedUnparseable($"JSON parse error: {ex.Message}");
            }
            if (json is null) return new Outcome.SkippedUnparseable("root is not a JSON object");

            // A present-but-different schemaVersion means the file belongs to a
            // different schema generation — don't graft this build's keys onto it.
            // (A MISSING schemaVersion is added by the migration: that converts a
            // file Load would reject outright into a working one.)
            if (json.TryGetPropertyValue("schemaVersion", out var sv) &&
                !(sv is JsonValue v && v.TryGetValue<int>(out var ver) && ver == ModSettings.CurrentSchemaVersion)) {
                return new Outcome.SkippedUnparseable("schemaVersion differs from this build; leaving file untouched");
            }

            var added = AddMissingKeys(json);
            if (added.Count == 0) return new Outcome.NoChange();

            WriteAtomic(path, json.ToJsonString(Indented), backupExisting: true);
            return new Outcome.AddedMissingKeys(path, added);
        } catch (Exception ex) {
            return new Outcome.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Same tmp/.bak/move shape as SettingsWriter.WriteAtomic; duplicated (4 lines)
    // so this class stays self-contained for the test project's Bootstrap include.
    private static void WriteAtomic(string path, string serialized, bool backupExisting) {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, serialized);
        if (backupExisting && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }
}

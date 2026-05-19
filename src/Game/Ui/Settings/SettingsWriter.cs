using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Atomic read-merge-write of slay_the_streamer_2.json. Preserves unknown keys
/// (forceL3PopupFallback, any future field) via JsonNode round-trip.
///
/// First-run path: if file doesn't exist, creates with schemaVersion + five
/// UI-managed fields. Credentials/YT must be added by JSON edit; UI doesn't
/// surface them.
/// </summary>
public static class SettingsWriter {
    public const int CurrentSchemaVersion = 1;

    public static void Write(string path, ChatSettings settings) {
        JsonObject json;
        if (File.Exists(path)) {
            var raw = File.ReadAllText(path);
            json = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
        } else {
            json = new JsonObject { ["schemaVersion"] = CurrentSchemaVersion };
        }

        // Overwrite the five UI-managed keys.
        json["voteDurationSeconds"] = settings.VoteDurationSeconds;
        json["voteOnActVariant"] = settings.VoteOnActVariant;
        json["cardSkipAsVoteOption"] = settings.CardSkipAsVoteOption;
        json["showVoteTag"] = settings.ShowVoteTag;
        json["cardSkipsPerAct"] = settings.CardSkipsPerAct;

        WriteAtomic(path, json);
    }

    private static void WriteAtomic(string path, JsonObject json) {
        var tmp = path + ".tmp";
        var bak = path + ".bak";
        var serialized = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, serialized);
        if (File.Exists(path)) {
            File.Copy(path, bak, overwrite: true);
        }
        // Move .tmp to path (overwriting). On Windows File.Move with overwrite is atomic.
        File.Move(tmp, path, overwrite: true);
    }
}

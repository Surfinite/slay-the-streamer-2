using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SlayTheStreamer2.Ti.Chat;

namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(string Channel, ChatCredentials Credentials);

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
            var warnings = new List<string>();

            var channel = ReadStringOrNull(root, "channel");
            var username = ReadStringOrNull(root, "username");
            var oauthToken = ReadStringOrNull(root, "oauthToken");

            if (string.IsNullOrWhiteSpace(channel)) return new SettingsResult.Malformed(path, "channel is missing or empty");
            if (string.IsNullOrWhiteSpace(username)) return new SettingsResult.Malformed(path, "username is missing or empty");
            if (string.IsNullOrWhiteSpace(oauthToken)) return new SettingsResult.Malformed(path, "oauthToken is missing or empty");

            var creds = new ChatCredentials(username, oauthToken);
            return new SettingsResult.Success(new ChatSettings(channel, creds), warnings);
        }
    }

    private static string? ReadStringOrNull(JsonElement root, string name) {
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }
}

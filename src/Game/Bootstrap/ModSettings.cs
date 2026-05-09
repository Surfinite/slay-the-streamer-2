using System.Collections.Generic;
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
        // Implementation in subsequent tasks.
        return new SettingsResult.Missing(path);
    }
}

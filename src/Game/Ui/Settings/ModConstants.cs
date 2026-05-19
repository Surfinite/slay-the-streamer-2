namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// String constants for the settings UI slice. Centralized to avoid
/// magic-string drift across SettingsPanelPatch, SettingsWriter,
/// RevealInExplorerAction, etc.
/// </summary>
internal static class ModConstants {
    /// <summary>
    /// Must match the `id` field in src/slay_the_streamer_2.json (mod manifest).
    /// Used by SettingsPanelPatch.Postfix to gate injection to our mod's row.
    /// </summary>
    public const string ModId = "slay_the_streamer_2";

    /// <summary>
    /// Settings file name under OS.GetUserDataDir() (resolves to %APPDATA%\SlayTheSpire2\ on Windows).
    /// </summary>
    public const string SettingsFileName = "slay_the_streamer_2.json";

    /// <summary>
    /// Stable child name for the injected settings panel. SettingsPanelPatch.Postfix
    /// removes any existing child by this name before adding a fresh one, to defend
    /// against duplicate-append on repeated Fill() calls.
    /// </summary>
    public const string SettingsPanelNodeName = "Sts2SettingsPanel";
}

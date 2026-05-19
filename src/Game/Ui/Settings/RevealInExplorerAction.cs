using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Opens the StS2 user-data directory in the OS file browser (Explorer on
/// Windows, Finder on macOS, default file manager on Linux). Discoverability
/// aid for streamers who need to edit JSON-only fields (credentials, etc.).
/// </summary>
internal static class RevealInExplorerAction {
    public static void Open() {
        try {
            var dir = OS.GetUserDataDir();
            var err = OS.ShellOpen(dir);
            if (err != Error.Ok) {
                TiLog.Warn($"[settings-ui] OS.ShellOpen returned {err} for {dir}");
            }
        } catch (System.Exception ex) {
            TiLog.Warn($"[settings-ui] RevealInExplorerAction.Open threw: {ex.Message}");
        }
    }
}

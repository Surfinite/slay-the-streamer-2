using System.IO;
using Godot;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// 500ms-debounced settings save. Each control change calls MarkDirtyAndRestart()
/// which updates ModSettings.Current immediately and (re-)arms a 500ms timer.
/// When the timer fires, if still dirty, the writer flushes to disk and the
/// dirty flag clears.
///
/// Attach as a child of the injected settings panel; the timer node is freed
/// automatically when the parent is freed. _ExitTree calls FlushNow as a
/// belt-and-braces safeguard if the timer hadn't fired yet.
/// </summary>
internal sealed partial class SettingsSaveDebouncer : Node {
    private const double DebounceSeconds = 0.5;
    private Timer? _timer;
    private bool _dirty;

    public override void _Ready() {
        _timer = new Timer {
            Name = "SettingsSaveDebouncerTimer",
            WaitTime = DebounceSeconds,
            OneShot = true,
            Autostart = false
        };
        _timer.Timeout += OnTimeout;
        AddChild(_timer);
    }

    public void MarkDirtyAndRestart(ChatSettings updated) {
        ModSettings.UpdateCurrent(updated);
        _dirty = true;
        _timer?.Stop();
        _timer?.Start();
    }

    public void FlushNow() {
        if (!_dirty) return;
        WriteAndClear();
    }

    private void OnTimeout() {
        if (!_dirty) return;
        WriteAndClear();
    }

    private void WriteAndClear() {
        var settings = ModSettings.Current;
        if (settings is null) return;
        var path = Path.Combine(OS.GetUserDataDir(), ModConstants.SettingsFileName);
        try {
            SettingsWriter.Write(path, settings);
            _dirty = false;
            TiLog.Info("[settings-ui] settings flushed to disk");
        } catch (System.Exception ex) {
            TiLog.Warn($"[settings-ui] settings flush failed: {ex.Message}");
            ShowToast("Failed to save settings.");
            // Leave _dirty = true so a subsequent change retries.
        }
    }

    /// <summary>
    /// Surface a short-lived status line at the bottom of our settings panel.
    /// Walks up from this Node to the panel root (Sts2SettingsPanel), then
    /// adds/reuses a named Label child so repeated failures replace each other
    /// rather than stacking. Auto-hides after 4 seconds via a one-shot Timer.
    /// </summary>
    private void ShowToast(string text) {
        Node? cursor = this;
        while (cursor != null && cursor.Name != ModConstants.SettingsPanelNodeName) {
            cursor = cursor.GetParent();
        }
        if (cursor is not Container panel) return;

        const string ToastName = "Sts2SettingsToast";
        var toast = panel.GetNodeOrNull<Label>(ToastName);
        if (toast is null) {
            toast = new Label {
                Name                = ToastName,
                HorizontalAlignment = HorizontalAlignment.Center,
                CustomMinimumSize   = new Vector2(0, 28),
                Modulate            = new Color(1f, 0.6f, 0.5f, 1f),
            };
            panel.AddChild(toast);
        }
        toast.Text    = text;
        toast.Visible = true;

        var hideTimer = new Timer { Name = "Sts2ToastHideTimer", WaitTime = 4.0, OneShot = true };
        hideTimer.Timeout += () => {
            if (GodotObject.IsInstanceValid(toast)) toast.Visible = false;
            if (GodotObject.IsInstanceValid(hideTimer)) hideTimer.QueueFree();
        };
        panel.AddChild(hideTimer);
        hideTimer.Start();
    }

    public override void _ExitTree() {
        // Belt-and-braces: flush any pending dirty state before the parent panel is freed.
        FlushNow();
        base._ExitTree();
    }
}

using Godot;
using SlayTheStreamer2.Game.Bootstrap;
using MegaCrit.Sts2.addons.mega_text;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Builds the settings panel Control imperatively. Hand-rolled MegaRichTextLabel
/// + stock Godot controls (CheckBox, HSlider, OptionButton, Button). Each
/// control's change event calls into the SettingsSaveDebouncer.
///
/// Style trade-off accepted: stylistic drift vs MegaCrit's vanilla settings
/// rows. Mitigation: keep visuals simple so drift is minimal.
///
/// Note on MegaRichTextLabel.AutoSizeEnabled: the class guards against using
/// AutoSizeEnabled together with FitContent (it would push a warning and disable
/// auto-size). We use FitContent = true for all labels here so that they size
/// to their content height; AutoSizeEnabled is therefore left at its default
/// (true) but will be overridden to false by the class itself when FitContent
/// is set. This is expected and harmless.
/// </summary>
internal static class SettingsPanelBuilder {
    public static Control Build(ChatSettings current, SettingsSaveDebouncer debouncer) {
        var root = new VBoxContainer {
            Name = ModConstants.SettingsPanelNodeName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        AddSeparator(root);
        AddHeader(root, "Vote behaviour");
        AddVoteDurationSlider(root, current, debouncer);
        AddCheckbox(root, "Vote on Act 1 variant", current.VoteOnActVariant,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { VoteOnActVariant = value }));
        AddCheckbox(root, "Allow chat to skip", current.CardSkipAsVoteOption,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { CardSkipAsVoteOption = value }));
        AddCheckbox(root, "Show vote tag", current.ShowVoteTag,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { ShowVoteTag = value }));

        AddSeparator(root);
        AddHeader(root, "Streamer");
        AddCardSkipsDropdown(root, current, debouncer);
        AddHelpText(root, "Number of card-reward skips the streamer can use per act. 0 = strict (no skips).");

        AddSeparator(root);
        AddHeader(root, "Settings file");
        AddFilePathRow(root);

        return root;
    }

    private static void AddSeparator(Container parent) {
        parent.AddChild(new HSeparator());
    }

    private static MegaRichTextLabel MakeLabel(string text) {
        var lbl = new MegaRichTextLabel {
            BbcodeEnabled = true,
            FitContent = true
        };
        lbl.Text = text;
        return lbl;
    }

    private static void AddHeader(Container parent, string text) {
        parent.AddChild(MakeLabel($"[b]{text}[/b]"));
    }

    private static void AddHelpText(Container parent, string text) {
        var lbl = MakeLabel($"[i]{text}[/i]");
        lbl.Modulate = new Color(0.7f, 0.7f, 0.7f);
        parent.AddChild(lbl);
    }

    private static void AddVoteDurationSlider(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        row.AddChild(MakeLabel("Vote duration"));

        var slider = new HSlider {
            MinValue = 10,
            MaxValue = 120,
            Step = 5,
            Value = current.VoteDurationSeconds,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        var badge = MakeLabel($"{current.VoteDurationSeconds}s");

        slider.ValueChanged += value => {
            badge.Text = $"{(int)value}s";
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { VoteDurationSeconds = (int)value });
        };

        row.AddChild(slider);
        row.AddChild(badge);
        parent.AddChild(row);
    }

    private static void AddCheckbox(Container parent, string text, bool initial, System.Action<bool> onChange) {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        row.AddChild(MakeLabel(text));

        var check = new CheckBox { ButtonPressed = initial };
        check.Toggled += pressed => onChange(pressed);
        row.AddChild(check);

        parent.AddChild(row);
    }

    private static void AddCardSkipsDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        row.AddChild(MakeLabel("Card skips per act (streamer's)"));

        var dropdown = new OptionButton();
        // (label, jsonValue) pairs.
        (string Label, int Value)[] entries = {
            ("0 (strict)", 0),
            ("1", 1),
            ("2", 2),
            ("3", 3),
            ("5", 5),
            ("Unlimited", -1)
        };

        int selectedIdx = -1;
        for (int i = 0; i < entries.Length; i++) {
            dropdown.AddItem(entries[i].Label, entries[i].Value);
            if (entries[i].Value == current.CardSkipsPerAct) selectedIdx = i;
        }

        // Legacy unsupported values (e.g. 4 from a hand-edited JSON) get a "Custom (N)" row.
        if (selectedIdx == -1) {
            dropdown.AddItem($"Custom ({current.CardSkipsPerAct})", current.CardSkipsPerAct);
            selectedIdx = dropdown.ItemCount - 1;
        }
        dropdown.Selected = selectedIdx;

        dropdown.ItemSelected += idx => {
            var id = (int)dropdown.GetItemId((int)idx);
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { CardSkipsPerAct = id });
        };

        row.AddChild(dropdown);
        parent.AddChild(row);
    }

    private static void AddFilePathRow(Container parent) {
        var path = System.IO.Path.Combine(OS.GetUserDataDir(), ModConstants.SettingsFileName);

        parent.AddChild(MakeLabel($"Settings file: [i]{path}[/i]"));

        var openBtn = new Button { Text = "Open folder" };
        openBtn.Pressed += () => RevealInExplorerAction.Open();
        parent.AddChild(openBtn);
    }
}

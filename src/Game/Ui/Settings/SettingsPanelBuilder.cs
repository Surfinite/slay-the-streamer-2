using Godot;
using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Builds the settings panel Control imperatively using plain Godot Label /
/// RichTextLabel nodes. MegaRichTextLabel is intentionally avoided here: its
/// SetTextAutoSize / AdjustFontSize logic binary-searches for the largest font
/// that fits the label's rect, and when the parent width is constrained (or
/// briefly zero before layout resolves) it can jump to MaxFontSize (100pt) and
/// wrap every character to its own line — exactly the catastrophic rendering
/// seen in the injected panel.
///
/// Plain Label with AutowrapMode = Word gives predictable wrapping; plain
/// RichTextLabel with BbcodeEnabled and explicit font-size overrides gives
/// predictable BBCode rendering without any auto-size machinery.
///
/// Style note: we skip vanilla's MegaCrit theme entirely for now. Controls
/// render in Godot's default theme, which is legible and non-intrusive.
/// </summary>
internal static class SettingsPanelBuilder {
    // Font size constants — keep consistent across headers, body text, and help text.
    private const int HeaderFontSize = 18;
    private const int BodyFontSize   = 14;
    private const int HelpFontSize   = 12;

    public static Control Build(ChatSettings current, SettingsSaveDebouncer debouncer) {
        var root = new VBoxContainer {
            Name = ModConstants.SettingsPanelNodeName,
            // ExpandFill so the VBoxContainer fills the ScrollContainer's horizontal space.
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
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
        AddHelpText(root, "Card-reward skips per act the streamer can use. 0 = strict (no skips).");

        AddSeparator(root);
        AddHeader(root, "Settings file");
        AddFilePathRow(root);

        return root;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static void AddSeparator(Container parent) {
        parent.AddChild(new HSeparator());
    }

    /// <summary>
    /// Short header label. Bold via a plain Label with a slightly larger font.
    /// Does NOT use BBCode or MegaRichTextLabel — no auto-size risk.
    /// </summary>
    private static void AddHeader(Container parent, string text) {
        var lbl = new Label {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
        };
        lbl.AddThemeFontSizeOverride("font_size", HeaderFontSize);
        parent.AddChild(lbl);
    }

    /// <summary>
    /// Small italic help text. Uses a plain Label with word-wrap.
    /// </summary>
    private static void AddHelpText(Container parent, string text) {
        var lbl = new Label {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate = new Color(0.75f, 0.75f, 0.75f),
            CustomMinimumSize = new Vector2(0, 20),
        };
        lbl.AddThemeFontSizeOverride("font_size", HelpFontSize);
        parent.AddChild(lbl);
    }

    /// <summary>
    /// Inline body label — used inside HBoxContainers as a row label.
    /// </summary>
    private static Label MakeBodyLabel(string text) {
        var lbl = new Label {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 24),
        };
        lbl.AddThemeFontSizeOverride("font_size", BodyFontSize);
        return lbl;
    }

    private static void AddVoteDurationSlider(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        row.AddChild(MakeBodyLabel("Vote duration"));

        var slider = new HSlider {
            MinValue = 10,
            MaxValue = 120,
            Step = 5,
            Value = current.VoteDurationSeconds,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 24),
        };

        // Value badge — plain Label, no auto-size.
        var badge = new Label {
            Text = $"{current.VoteDurationSeconds}s",
            AutowrapMode = TextServer.AutowrapMode.Off,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(36, 24),
        };
        badge.AddThemeFontSizeOverride("font_size", BodyFontSize);

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

        row.AddChild(MakeBodyLabel(text));

        var check = new CheckBox {
            ButtonPressed = initial,
            CustomMinimumSize = new Vector2(0, 24),
        };
        check.Toggled += pressed => onChange(pressed);
        row.AddChild(check);

        parent.AddChild(row);
    }

    private static void AddCardSkipsDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        row.AddChild(MakeBodyLabel("Card skips per act (streamer's)"));

        var dropdown = new OptionButton {
            CustomMinimumSize = new Vector2(110, 24),
        };

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

        var pathLbl = new Label {
            Text = $"Path: {path}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 20),
        };
        pathLbl.AddThemeFontSizeOverride("font_size", HelpFontSize);
        parent.AddChild(pathLbl);

        var openBtn = new Button {
            Text = "Open folder",
            CustomMinimumSize = new Vector2(0, 28),
        };
        openBtn.Pressed += () => RevealInExplorerAction.Open();
        parent.AddChild(openBtn);
    }
}

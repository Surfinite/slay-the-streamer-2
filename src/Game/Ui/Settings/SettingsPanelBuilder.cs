using Godot;
using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2.Game.Ui.Settings;

/// <summary>
/// Builds the settings panel Control imperatively using plain Godot controls
/// styled to match vanilla NSettings* rows.
///
/// Styling approach (theme-injection, not scene instantiation):
///   - Vanilla row labels use kreon_regular_shared.tres at font_size=22 (slightly
///     below vanilla's 28 because our panel area is smaller) with warm-white colour.
///   - Vanilla dividers are ColorRect h=2 with the standard muted-gold tint.
///   - Vanilla row containers are MarginContainer with custom_minimum_size h=52
///     and 12px left/right margin.
///   - Vanilla button labels use kreon_bold_glyph_space_two.tres with the same
///     warm-white colour and a dark outline.
///
/// We avoid MegaRichTextLabel throughout: its SetTextAutoSize binary-search can
/// jump to MaxFontSize (100pt) when the parent rect is briefly zero during layout,
/// wrapping every character to its own line.
///
/// Vanilla scene instantiation is skipped because NSettingsSlider and
/// NSettingsDropdown are abstract (subclassed per-option), and NSettingsTickbox
/// has no Label node — vanilla always pairs it with a separate RichTextLabel
/// in the same MarginContainer row.
/// </summary>
internal static class SettingsPanelBuilder {
    // Vanilla settings row labels are font_size=28; ours are smaller because the
    // panel area is narrower. 22 fits well with the 52px row height.
    private const int RowFontSize  = 22;
    private const int HelpFontSize = 15;

    // Warm-white used for vanilla row labels.
    // From settings_screen.tscn: theme_override_colors/font_color for SliderValue
    //   = Color(1, 0.964706, 0.886275, 1)
    private static readonly Color LabelColor  = new Color(1f, 0.965f, 0.886f, 1f);
    private static readonly Color HelpColor   = new Color(0.7f, 0.7f, 0.7f, 1f);
    // Vanilla divider colour from settings_screen.tscn ColorRect nodes.
    private static readonly Color DividerColor = new Color(0.910f, 0.863f, 0.745f, 0.251f);

    // Kreon font paths (vanilla resource paths, loaded at runtime).
    private const string KreonRegularPath  = "res://themes/kreon_regular_shared.tres";
    private const string KreonBoldPath     = "res://themes/kreon_bold_glyph_space_two.tres";
    // Button background image used by vanilla Credits / Feedback buttons.
    private const string ButtonBgPath      = "res://images/ui/reward_screen/reward_skip_button.png";
    // Vanilla tickbox icons — AtlasTexture resources from ui_atlas_0.png (64×64 each).
    private const string TickboxCheckedPath   = "res://images/atlases/ui_atlas.sprites/checkbox_ticked.tres";
    private const string TickboxUncheckedPath = "res://images/atlases/ui_atlas.sprites/checkbox_unticked.tres";

    // Cached font references — loaded once, reused for every row.
    private static Font? _kreonRegular;
    private static Font? _kreonBold;
    private static Texture2D? _buttonBg;
    private static Texture2D? _tickboxChecked;
    private static Texture2D? _tickboxUnchecked;

    public static Control Build(ChatSettings current, SettingsSaveDebouncer debouncer) {
        // Load vanilla fonts; if resource loading fails we fall back to null
        // (Godot will use its default font, which is still legible).
        _kreonRegular     ??= TryLoadFont(KreonRegularPath);
        _kreonBold        ??= TryLoadFont(KreonBoldPath);
        _buttonBg         ??= TryLoadTexture(ButtonBgPath);
        _tickboxChecked   ??= TryLoadTexture(TickboxCheckedPath);
        _tickboxUnchecked ??= TryLoadTexture(TickboxUncheckedPath);

        var root = new VBoxContainer {
            Name = ModConstants.SettingsPanelNodeName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        AddDivider(root);
        AddVoteDurationSlider(root, current, debouncer);
        AddDivider(root);
        AddCheckboxRow(root, "Vote on Act 1 variant", current.VoteOnActVariant,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { VoteOnActVariant = value }));
        AddDivider(root);
        AddCheckboxRow(root, "Allow chat to skip", current.CardSkipAsVoteOption,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { CardSkipAsVoteOption = value }));
        AddDivider(root);
        AddCheckboxRow(root, "Show vote tag", current.ShowVoteTag,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { ShowVoteTag = value }));
        AddHelpText(root, "Displays and increments a tag for each vote, e.g.  [b][14][/b]\nChat can vote with [b]#0!14[/b] so delayed votes don't land in the wrong tally.\nCould be useful to combat lag on YT, (might just be confusing).");
        AddDivider(root);
        AddCardSkipsDropdown(root, current, debouncer);
        AddHelpText(root, "Card-rewards streamer can skip before initiating a vote.\nSkips reset each act.");
        AddDivider(root);
        AddFilePathRow(root);

        return root;
    }

    // -------------------------------------------------------------------------
    // Row factories — each produces a vanilla-style MarginContainer row
    // -------------------------------------------------------------------------

    private static void AddVoteDurationSlider(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        inner.AddChild(MakeRowLabel("Vote duration"));

        var slider = new HSlider {
            MinValue = 10,
            MaxValue = 120,
            Step = 5,
            Value = current.VoteDurationSeconds,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical   = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize   = new Vector2(0, 20),
        };

        var badge = new Label {
            Text               = $"{current.VoteDurationSeconds}s",
            AutowrapMode       = TextServer.AutowrapMode.Off,
            VerticalAlignment  = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            CustomMinimumSize  = new Vector2(44, 0),
        };
        ApplyRowFont(badge);

        slider.ValueChanged += value => {
            badge.Text = $"{(int)value}s";
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { VoteDurationSeconds = (int)value });
        };

        inner.AddChild(slider);
        inner.AddChild(badge);
        parent.AddChild(row);
    }

    private static void AddCheckboxRow(Container parent, string text, bool initial, System.Action<bool> onChange) {
        var row   = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        inner.AddChild(MakeRowLabel(text));

        var check = new CheckBox {
            ButtonPressed     = initial,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(40, 40),
        };
        // Apply vanilla tickbox icons so the checkbox is the right size and clearly
        // visible in both states. The atlas textures are 64×64; Godot scales them
        // to fit the icon slot, which the CheckBox sizes to CustomMinimumSize.
        if (_tickboxChecked   != null) check.AddThemeIconOverride("checked",   _tickboxChecked);
        if (_tickboxUnchecked != null) check.AddThemeIconOverride("unchecked", _tickboxUnchecked);
        check.Toggled += pressed => onChange(pressed);
        inner.AddChild(check);

        parent.AddChild(row);
    }

    private static void AddCardSkipsDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row   = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        inner.AddChild(MakeRowLabel("Streamer card skips / act"));

        var dropdown = new OptionButton {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(115, 0),
        };
        if (_kreonRegular != null) dropdown.AddThemeFontOverride("font", _kreonRegular);
        dropdown.AddThemeFontSizeOverride("font_size", 22);

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
        if (selectedIdx == -1) {
            dropdown.AddItem($"Custom ({current.CardSkipsPerAct})", current.CardSkipsPerAct);
            selectedIdx = dropdown.ItemCount - 1;
        }
        dropdown.Selected = selectedIdx;

        // Apply Kreon font to the popup menu so opened items match the button.
        var popup = dropdown.GetPopup();
        if (_kreonRegular != null) popup.AddThemeFontOverride("font", _kreonRegular);
        popup.AddThemeFontSizeOverride("font_size", 22);

        dropdown.ItemSelected += idx => {
            var id = (int)dropdown.GetItemId((int)idx);
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { CardSkipsPerAct = id });
        };

        inner.AddChild(dropdown);
        parent.AddChild(row);
    }

    private static void AddFilePathRow(Container parent) {
        var path = System.IO.Path.Combine(OS.GetUserDataDir(), ModConstants.SettingsFileName).Replace('/', '\\');
        // "Open folder" button styled like the vanilla Credits button:
        // reward_skip_button.png background + Kreon bold label.


        var openBtn = new Button {
            Text              = "Open settings folder",
            CustomMinimumSize = new Vector2(0, 48),
        };

        ApplyButtonStyle(openBtn);
        openBtn.Pressed += () => RevealInExplorerAction.Open();
        var mc = new MarginContainer {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        mc.AddThemeConstantOverride("margin_left",   0);
        mc.AddThemeConstantOverride("margin_top",    18);
        mc.AddThemeConstantOverride("margin_right",  0);
        mc.AddThemeConstantOverride("margin_bottom", 0);
        mc.AddChild(openBtn);
        parent.AddChild(mc);

        var pathLbl = new Label {
            Text                = path,
            AutowrapMode        = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize   = new Vector2(0, 20),
            Modulate            = HelpColor,
        };
        ApplyFont(pathLbl, _kreonRegular, HelpFontSize);
        parent.AddChild(pathLbl);

    }

    // -------------------------------------------------------------------------
    // Help text (small, muted, word-wrapped — e.g. below dropdowns)
    // -------------------------------------------------------------------------

    private static void AddHelpText(Container parent, string text) {

        var lbl = new RichTextLabel {
            Text                = text,
            BbcodeEnabled       = true,
            FitContent          = true,
            ScrollActive        = false,
            AutowrapMode        = TextServer.AutowrapMode.Word,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate            = HelpColor,
            CustomMinimumSize   = new Vector2(0, 20),
        };

        if (_kreonRegular != null) lbl.AddThemeFontOverride("normal_font", _kreonRegular);
        if (_kreonBold    != null) lbl.AddThemeFontOverride("bold_font",   _kreonBold);
        lbl.AddThemeFontSizeOverride("normal_font_size", HelpFontSize);
        lbl.AddThemeFontSizeOverride("bold_font_size",   HelpFontSize);

        var mc = new MarginContainer {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        mc.AddThemeConstantOverride("margin_left",   12);
        mc.AddThemeConstantOverride("margin_top",    -15);
        mc.AddThemeConstantOverride("margin_right",  130);
        mc.AddThemeConstantOverride("margin_bottom", 12);
        mc.AddChild(lbl);
        parent.AddChild(mc);
    }

    // -------------------------------------------------------------------------
    // Vanilla-style divider (muted gold 2px line)
    // -------------------------------------------------------------------------

    private static void AddDivider(Container parent) {
        var div = new ColorRect {
            Color             = DividerColor,
            CustomMinimumSize = new Vector2(0, 2),
            MouseFilter       = Control.MouseFilterEnum.Ignore,
        };
        parent.AddChild(div);
    }

    // -------------------------------------------------------------------------
    // Row builder: MarginContainer (h=52) → HBoxContainer → [label, control]
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a MarginContainer with a single HBoxContainer child.
    /// Callers append their label + control into the HBoxContainer.
    /// </summary>
    private static MarginContainer MakeRow() {
        var mc = new MarginContainer {
            CustomMinimumSize = new Vector2(0, 52),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        mc.AddThemeConstantOverride("margin_left",   12);
        mc.AddThemeConstantOverride("margin_top",    0);
        mc.AddThemeConstantOverride("margin_right",  12);
        mc.AddThemeConstantOverride("margin_bottom", 0);

        var hbox = new HBoxContainer {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
        };
        mc.AddChild(hbox);
        return mc;
    }

    /// <summary>
    /// Row label: ExpandFill, vertically centred, Kreon Regular, warm-white.
    /// </summary>
    private static Label MakeRowLabel(string text) {
        var lbl = new Label {
            Text                = text,
            AutowrapMode        = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        ApplyRowFont(lbl);
        return lbl;
    }

    // -------------------------------------------------------------------------
    // Style helpers
    // -------------------------------------------------------------------------

    private static void ApplyRowFont(Label lbl) {
        ApplyFont(lbl, _kreonRegular, RowFontSize);
        lbl.AddThemeColorOverride("font_color", LabelColor);
    }

    private static void ApplyFont(Label lbl, Font? font, int size) {
        if (font != null) lbl.AddThemeFontOverride("font", font);
        lbl.AddThemeFontSizeOverride("font_size", size);
    }

    /// <summary>
    /// Style a Button to look like vanilla's Credits/Feedback buttons:
    /// reward_skip_button.png background, Kreon bold label, warm-white text.
    /// </summary>
    private static void ApplyButtonStyle(Button btn) {
        // Vanilla button text styling.
        if (_kreonBold != null) btn.AddThemeFontOverride("font", _kreonBold);
        btn.AddThemeFontSizeOverride("font_size", RowFontSize);
        btn.AddThemeColorOverride("font_color",          LabelColor);
        btn.AddThemeColorOverride("font_hover_color",    LabelColor);
        btn.AddThemeColorOverride("font_pressed_color",  LabelColor);
        btn.AddThemeColorOverride("font_focus_color",    LabelColor);
        btn.AddThemeColorOverride("font_disabled_color", new Color(LabelColor, 0.5f));
        // Dark outline, matching vanilla button labels.
        btn.AddThemeConstantOverride("outline_size", 8);
        btn.AddThemeColorOverride("font_outline_color", new Color(0.13f, 0.10f, 0.06f, 1f));

        // If the background texture loaded, use it via a StyleBoxTexture.
        if (_buttonBg != null) {
            var normal = new StyleBoxTexture { Texture = _buttonBg };
            btn.AddThemeStyleboxOverride("normal",   normal);
            btn.AddThemeStyleboxOverride("hover",    normal);
            btn.AddThemeStyleboxOverride("pressed",  normal);
            btn.AddThemeStyleboxOverride("focus",    normal);
            btn.AddThemeStyleboxOverride("disabled", normal);
        }
    }

    // -------------------------------------------------------------------------
    // Resource loading with graceful fallback
    // -------------------------------------------------------------------------

    private static Font? TryLoadFont(string resPath) {
        try {
            return ResourceLoader.Exists(resPath)
                ? ResourceLoader.Load<Font>(resPath)
                : null;
        } catch {
            return null;
        }
    }

    private static Texture2D? TryLoadTexture(string resPath) {
        try {
            return ResourceLoader.Exists(resPath)
                ? ResourceLoader.Load<Texture2D>(resPath)
                : null;
        } catch {
            return null;
        }
    }
}

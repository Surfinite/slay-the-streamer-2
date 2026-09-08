using System;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Events;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.4. An event option whose text carries the appended line is
/// squeezed by the button's fixed 100 px height (the %Text label auto-shrinks its font).
/// Grow the button instead: measure the full text at the vanilla 24 px font wrapped to
/// the label width, add the overflow to the button's minimum height, the label box and
/// the vertically centred nine-patches (Image, Outline, RedFlash, BlueFlash) and move
/// PlayerVoteContainer down. Regular buttons have %Text as a direct child; Ancient
/// buttons wrap it in an HBoxContainer. Null-tolerant: a scene rename degrades to the
/// vanilla squeeze.</summary>
[HarmonyPatch(typeof(NEventOptionButton), "_Ready")]
internal static class EventOptionGrowPatch {
    private const float VanillaHeight = 100f;
    private const float LabelInset = 13f;
    private const int VanillaFontSize = 24;
    private const float Margin = 6f;
    private static readonly Regex Tags = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);
    private static readonly string Marker = AuthorityLoc.Lead.TrimStart('\n');

    static void Postfix(NEventOptionButton __instance) {
        try {
            var label = __instance.GetNodeOrNull<MegaRichTextLabel>("%Text");
            if (label is null) return;
            string text = label.Text ?? "";
            if (!text.Contains(Marker, StringComparison.Ordinal)) return;

            float available = VanillaHeight - 2f * LabelInset;
            float width = label.Size.X > 0 ? label.Size.X : label.CustomMinimumSize.X;
            if (width <= 0) width = 722f;
            float needed = Measure(label, text, width);
            float extra = Mathf.Ceil(Mathf.Max(0f, needed + Margin - available));
            if (extra <= 0f) return;

            var min = __instance.CustomMinimumSize;
            __instance.CustomMinimumSize = new Vector2(min.X, VanillaHeight + extra);
            __instance.PivotOffset = new Vector2(__instance.PivotOffset.X, (VanillaHeight + extra) * 0.5f);

            if (label.GetParent() is HBoxContainer hbox) {
                hbox.OffsetBottom += extra;
                var lm = label.CustomMinimumSize;
                label.CustomMinimumSize = new Vector2(lm.X, lm.Y + extra);
            } else {
                label.OffsetBottom += extra;
            }

            foreach (var name in new[] { "Image", "Outline", "RedFlash", "BlueFlash" }) {
                if (__instance.GetNodeOrNull<Control>(name) is { } piece) {
                    piece.OffsetTop -= extra * 0.5f;
                    piece.OffsetBottom += extra * 0.5f;
                    piece.PivotOffset = new Vector2(piece.PivotOffset.X, piece.PivotOffset.Y + extra * 0.5f);
                }
            }
            if (__instance.GetNodeOrNull<Control>("PlayerVoteContainer") is { } votes) {
                votes.OffsetTop += extra;
                votes.OffsetBottom += extra;
            }
            TiLog.Info($"[SlayTheStreamer2][card-scope] event option grown by {extra}px for the explanation line (needed={needed:0} available={available:0})");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] event option grow failed (vanilla squeeze remains)", ex); }
    }

    private static float Measure(MegaRichTextLabel label, string text, float width) {
        string plain = Tags.Replace(text, "");
        try {
            var font = label.GetThemeFont("normal_font", "RichTextLabel");
            if (font is not null) return font.GetMultilineStringSize(plain, HorizontalAlignment.Left, width, VanillaFontSize).Y;
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] event option measure fell back to an estimate: {ex.Message}"); }
        int charsPerLine = Mathf.Max(20, (int)(width / 12f));
        int lines = 0;
        foreach (var para in plain.Split('\n')) lines += Mathf.Max(1, (para.Length + charsPerLine - 1) / charsPerLine);
        return lines * (VanillaFontSize + 2f);
    }
}

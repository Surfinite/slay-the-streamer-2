using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>The removal look (spec section 3): removed = solid red, one 0.35 s tween,
/// no pulse, no caption. Visual only: catch + log.</summary>
internal static class RemovalVisuals {
    internal static readonly Color RemovedRed = new(1f, 0.28f, 0.28f, 1f);

    internal static Tween? PaintRemoved(Control target, Node tweenOwner) {
        try {
            if (!GodotObject.IsInstanceValid(target) || !GodotObject.IsInstanceValid(tweenOwner)) return null;
            var tween = tweenOwner.CreateTween();
            tween.TweenProperty(target, "modulate", RemovedRed, 0.35).SetTrans(Tween.TransitionType.Sine);
            return tween;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] removed paint failed", ex); return null; }
    }

    /// <summary>Card holders keep hover/inspect alive when unclickable; buttons are
    /// fully disabled (also unregisters their hotkeys).</summary>
    internal static void SetClickable(Control option, bool clickable) {
        try {
            if (!GodotObject.IsInstanceValid(option)) return;
            switch (option) {
                case NCardHolder holder: holder.SetClickable(clickable); break;
                case NClickableControl button: if (clickable) button.Enable(); else button.Disable(); break;
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] clickable toggle failed", ex); }
    }
}

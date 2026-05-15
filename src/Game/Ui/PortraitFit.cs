using System;
using System.Numerics;

namespace SlayTheStreamer2.Game.Ui;

internal static class PortraitFit {
    /// <summary>
    /// Computes a uniform scale factor to fit a sprite of <paramref name="boundsSize"/>
    /// inside a slot of <paramref name="slotSize"/>, never upscaling past native size.
    /// Defensive against zero, negative, or sub-1.0 positive bounds (returns 1.0).
    /// </summary>
    public static float ComputeFitScale(Vector2 boundsSize, Vector2 slotSize) {
        var fit = MathF.Min(
            slotSize.X / MathF.Max(boundsSize.X, 1f),
            slotSize.Y / MathF.Max(boundsSize.Y, 1f));
        return MathF.Min(fit, 1f);
    }
}

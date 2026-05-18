namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Render mode for ActVariantVotePopup. Determined by PreWarmAssets:
/// L1Textures when all 4 assets loaded; L3Fallback when any failed or
/// ForceL3PopupFallback setting is true.
/// </summary>
internal enum ActVariantPopupMode {
    L1Textures,
    L3Fallback,
}

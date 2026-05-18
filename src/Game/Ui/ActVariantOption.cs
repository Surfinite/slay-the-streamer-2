namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// DTO for one act-variant column in the popup. Carries primitives only —
/// no Godot, no MegaCrit references, so it's reachable from the
/// Microsoft.NET.Sdk-based test project. Nullable asset paths allow L3
/// fallback when assets aren't located during the spike.
///
/// FallbackColorHex format: 6-digit RRGGBB (no leading '#', no alpha).
/// Sourced from each ActModel.MapBgColor.
/// </summary>
internal readonly record struct ActVariantOption(
    int Index,
    string Key,
    string Title,
    string? BackgroundPath,
    string? BannerPath,
    string FallbackColorHex);

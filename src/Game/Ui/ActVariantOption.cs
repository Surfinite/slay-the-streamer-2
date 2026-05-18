namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// DTO for one act-variant column in the popup. Carries primitives only —
/// no Godot, no MegaCrit references, so it's reachable from the
/// Microsoft.NET.Sdk-based test project.
///
/// L1 rendering (full layered combat-backdrop scene) is driven by a
/// Func&lt;Godot.Node&gt; factory built in ActVariantVotePatch using the
/// Key as the BackgroundAssets title argument. The factory is passed to
/// the popup as a separate parameter — NOT carried in this DTO — to keep
/// the DTO MegaCrit/Godot-free for unit tests.
///
/// FallbackColorHex format: 6-digit RRGGBB (no leading '#', no alpha).
/// Sourced from each ActModel.MapBgColor. Used by L3 mode (text-only
/// rendering with a flat color backdrop) when the factory is null or
/// invocation fails.
/// </summary>
internal readonly record struct ActVariantOption(
    int Index,
    string Key,
    string Title,
    string FallbackColorHex);

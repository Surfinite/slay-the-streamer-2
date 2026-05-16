using System;
using Godot;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// MegaCrit-free DTO for boss-vote popup column data. BossVotePatch maps
/// MegaCrit.Sts2 EncounterModel → BossVotePopupOption before constructing
/// the popup, so BossVotePopup never references MegaCrit types at the
/// public interface level.
///
/// VisualsFactory is invoked once per column at popup.Show() time on the
/// Godot main thread. The returned Node2D is parented under the column's
/// portrait slot. Lazy invocation: if Show() is never called (e.g., the
/// session is cancelled mid-construction), no NCreatureVisuals instances
/// are created — nothing to leak.
/// </summary>
internal sealed record BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory);

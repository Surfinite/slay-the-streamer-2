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
///
/// MarkPriorWinner is set only on the A10 second-boss round when the
/// "allow same boss twice" setting (task 2b) re-offers the round-1 winner:
/// the popup renders a "won round 1" badge on that column so chat understands
/// that re-picking it means fighting the same boss twice.
/// </summary>
internal sealed record BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory, bool MarkPriorWinner = false);

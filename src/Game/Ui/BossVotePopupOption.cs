namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Game-free DTO for boss-vote popup column data. BossVotePatch maps
/// MegaCrit.Sts2 EncounterModel → BossVotePopupOption before constructing
/// the popup, so BossVotePopup never references MegaCrit types.
/// </summary>
internal sealed record BossVotePopupOption(int Index, string Title, string? PortraitPath);

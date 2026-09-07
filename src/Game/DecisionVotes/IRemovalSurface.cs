// src/Game/DecisionVotes/IRemovalSurface.cs
using System.Collections.Generic;
using Godot;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>What RemovalVoteFlow needs from a screen. Implemented for the card-reward
/// screen (CardRewardRemovalSurface) and the choose-a-card screen
/// (ChooseACardRemovePatch.Surface). All members run on the main thread.</summary>
internal interface IRemovalSurface {
    Node ScreenNode { get; }
    object RecordKey { get; }
    string LogTag { get; }
    bool HasSkip { get; }
    IReadOnlyList<string> CardTitles();
    IReadOnlyList<Control> CardHolders();
    Control? SkipControl();
    Control? BannerAnchor();
    object? SnapshotOptions();
    bool OptionsMatch(object? snapshot);
}

// src/Game/DecisionVotes/RemovalRecords.cs
using System.Runtime.CompilerServices;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>What chat removed on one reward (spec section 3.1). RemovedIndex is a card
/// holder index or RemovalClickRules.SkipIndex. OptionsSnapshot is surface-defined
/// (the CardCreationResult array for the reward screen; null for choose-a-card, whose
/// option list never changes).</summary>
internal sealed record RemovalRecord(int RemovedIndex, string RemovedLabel, object? OptionsSnapshot) {
    public bool IsSkip => RemovedIndex == RemovalClickRules.SkipIndex;
}

/// <summary>Keyed on the reward object (or the choose-a-card context), not the screen,
/// so ESC-and-reopen shows the same removal without a new vote.</summary>
internal static class RemovalRecords {
    private static readonly ConditionalWeakTable<object, RemovalRecord> Table = new();
    internal static RemovalRecord? Get(object key) => Table.TryGetValue(key, out var r) ? r : null;
    internal static void Set(object key, RemovalRecord record) => Table.AddOrUpdate(key, record);
    internal static void Clear(object key) => Table.Remove(key);
}

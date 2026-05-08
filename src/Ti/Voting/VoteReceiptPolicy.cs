using System;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Controls receipt cadence/announcements.
/// PeriodicTallyEvery semantics: null = adaptive (max(7s, duration/5));
/// TimeSpan.Zero = no periodic tally; positive value = fixed cadence.
/// </summary>
public sealed record VoteReceiptPolicy(
    bool AnnounceOnOpen = true,
    TimeSpan? PeriodicTallyEvery = null,
    bool AnnounceOnClose = true) {
    public static VoteReceiptPolicy Default => new();
    public static VoteReceiptPolicy Silent => new(false, TimeSpan.Zero, false);
    public static VoteReceiptPolicy WithFixedCadence(TimeSpan cadence) => new(true, cadence, true);
}

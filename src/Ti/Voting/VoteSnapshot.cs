using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Read-only view of a vote at a moment in time. EnglishReceipts and the UI
/// overlay both consume this; VoteSession produces it. Decoupling the formatter
/// from VoteSession makes EnglishReceipts unit-testable without spinning up
/// a session.
/// </summary>
public sealed record VoteSnapshot(
    string Id,
    string Label,
    IReadOnlyList<VoteOption> Options,
    TimeSpan Duration,
    TimeSpan TimeRemaining,
    IReadOnlyDictionary<int, int> Tallies,
    VoteSessionState State,
    int? WinnerIndex,
    int? RandomTieAmong,                  // when WinnerIndex was picked from a tie, how many options were tied
    bool NoVotesReceived,                 // true if WinnerIndex was picked from all options because zero votes came in
    TimeSpan DisconnectGap,               // total time chat was offline during the vote (TimeSpan.Zero if none)
    int VoteId                            // cycling 0..99 nonce assigned by VoteCoordinator; lets receipts/UI disambiguate consecutive votes
);

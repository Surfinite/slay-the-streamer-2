using System;
using System.Collections.Generic;
using System.Threading;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Process-wide static facade over a VoteCoordinator. Set Voter.Default once
/// from ModEntry.Initialize (Plan B); Harmony-patch call sites then use
/// Voter.Start(...) without plumbing a coordinator reference everywhere.
///
/// For multiplayer / multi-channel scenarios, construct VoteCoordinator
/// instances directly instead of using this facade.
/// </summary>
public static class Voter {
    public static VoteCoordinator? Default { get; set; }

    public static VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        bool showTag = true,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt = null,
        CancellationToken ct = default) {
        var coord = Default
            ?? throw new InvalidOperationException(
                "Voter.Default is not initialised. Set it from ModEntry.Initialize.");
        return coord.Start(label, options, duration, showTag, receipts, parsing, formatReceipt, ct);
    }
}

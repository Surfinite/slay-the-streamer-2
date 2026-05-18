using System;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Custom formatter passed to VoteCoordinator.Start(formatReceipt: ...) for
/// the act-variant vote. Substitutes accurate close-receipt text when no
/// votes are received (the generic FormatClose would falsely claim chat
/// chose a random option). All other ReceiptKind cases delegate to
/// EnglishReceipts.
///
/// The onNoVotes callback is invoked when the no-votes Close branch fires,
/// side-channeling the outcome to the patch's HandleVoteAsync so it can
/// distinguish "no votes → preserve vanilla random pick" from "real winner"
/// without requiring a public VoteSession.Snapshot accessor (which doesn't
/// exist in the current Ti layer).
/// </summary>
internal static class ActVariantReceiptFormatter {

    internal const string NoVotesCloseText =
        "Act 1 variant vote closed: no votes received — vanilla random pick stands.";

    internal static string Format(VoteSnapshot snapshot, ReceiptKind kind, Action onNoVotes) {
        if (kind == ReceiptKind.Close && snapshot.NoVotesReceived) {
            onNoVotes();
            return NoVotesCloseText;
        }
        return kind switch {
            ReceiptKind.Open          => EnglishReceipts.FormatOpen(snapshot),
            ReceiptKind.PeriodicTally => EnglishReceipts.FormatPeriodicTally(snapshot),
            ReceiptKind.Close         => EnglishReceipts.FormatClose(snapshot),
            _                         => EnglishReceipts.FormatClose(snapshot),
        };
    }
}

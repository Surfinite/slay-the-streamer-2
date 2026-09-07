using System;
using System.Linq;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Chat receipts for a removal vote (spec section 3). Pure functions; the
/// streamer name is passed in so the class stays testable without settings.</summary>
public static class RemoveVoteReceipts {
    public static string Format(VoteSnapshot s, ReceiptKind kind, string streamerName) => kind switch {
        ReceiptKind.Open => FormatOpen(s, streamerName),
        ReceiptKind.Close => FormatClose(s, streamerName),
        _ => EnglishReceipts.FormatPeriodicTally(s),
    };

    public static string FormatOpen(VoteSnapshot s, string streamerName) {
        var numbers = string.Join(", ", s.Options.Select(o => o.Index.ToString()));
        var tag = s.ShowTag ? $" [{s.VoteId:D2}]" : "";
        return $"Vote{tag}: remove one option from {streamerName}'s card reward! Type {numbers}. {(int)s.Duration.TotalSeconds}s left.";
    }

    public static string FormatClose(VoteSnapshot s, string streamerName) {
        if (s.WinnerIndex is not int idx) return "Vote: removal closed without a result.";
        var label = s.Options.First(o => o.Index == idx).Label;
        string outcome = label == CardRewardOptionLabels.SkipLabel
            ? $"{streamerName} must take a card."
            : $"{streamerName} picks from the rest.";
        string body;
        if (s.NoVotesReceived) body = $"No votes received. Chat removed {idx}: {label} randomly.";
        else if (s.RandomTieAmong is int tied && tied >= 3) body = $"{tied}-way tie! Chat removed {idx}: {label} randomly.";
        else if (s.RandomTieAmong is int) {
            var max = s.Tallies.Values.Max();
            var tiedLabels = string.Join(" and ", s.Tallies.Where(kv => kv.Value == max)
                .Select(kv => $"{kv.Key} {s.Options.First(o => o.Index == kv.Key).Label}"));
            body = $"Tie between {tiedLabels}. Chat removed {idx}: {label} randomly.";
        } else body = $"Chat removed {idx}: {label}.";
        if (s.DisconnectGap > TimeSpan.Zero)
            body = body.TrimEnd('.') + $" (chat was offline {(int)s.DisconnectGap.TotalSeconds}s during voting).";
        return body + " " + outcome;
    }

    public static string FormatOverride(string streamerName, string takenLabel, int limit, int remaining, string? curseTitle) =>
        VoteOverrideBudget.FormatOverrideText(streamerName, "overrode chat's removal", takenLabel, limit, remaining, curseTitle);
}

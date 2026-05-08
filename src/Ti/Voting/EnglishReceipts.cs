using System;
using System.Linq;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Default English receipt text. Pure functions from VoteSnapshot to string.
/// Future i18n: add a peer SpanishReceipts.cs (etc.) and pass a delegate to
/// VoteCoordinator.Start to override.
/// </summary>
public static class EnglishReceipts {
    public static string FormatOpen(VoteSnapshot s) {
        var numbers = string.Join(", ", s.Options.Select(o => o.Index.ToString()));
        return $"Vote: {s.Label}! Type {numbers} — {(int)s.Duration.TotalSeconds}s left.";
    }

    public static string FormatPeriodicTally(VoteSnapshot s) {
        var counts = string.Join(" ", s.Options.Select(o =>
            $"{o.Index}={(s.Tallies.TryGetValue(o.Index, out var c) ? c : 0)}"));
        return $"Vote: {counts}, {(int)s.TimeRemaining.TotalSeconds}s left.";
    }

    public static string FormatClose(VoteSnapshot s) {
        if (s.WinnerIndex is not int winnerIdx)
            return $"Vote: {s.Label} closed without a winner.";   // shouldn't happen on natural close

        var winnerLabel = s.Options.First(o => o.Index == winnerIdx).Label;
        string body;

        if (s.NoVotesReceived) {
            body = $"No votes received — chat got {winnerIdx}: {winnerLabel} randomly.";
        } else if (s.RandomTieAmong is int tied) {
            if (tied >= 3) {
                body = $"{tied}-way tie! Chat chose {winnerIdx}: {winnerLabel} randomly.";
            } else {
                // tied == 2: name the two tied options
                var tiedLabels = string.Join(" and ", s.Tallies
                    .Where(kv => kv.Value == s.Tallies.Values.Max())
                    .Select(kv => $"{kv.Key} {s.Options.First(o => o.Index == kv.Key).Label}"));
                body = $"Tie between {tiedLabels} — chat chose {winnerIdx}: {winnerLabel} randomly.";
            }
        } else {
            body = $"Chat chose {winnerIdx}: {winnerLabel}.";
        }

        if (s.DisconnectGap > TimeSpan.Zero) {
            body = body.TrimEnd('.') + $" (chat was offline {(int)s.DisconnectGap.TotalSeconds}s during voting).";
        }
        return body;
    }
}

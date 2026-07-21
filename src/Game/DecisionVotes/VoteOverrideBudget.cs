using System;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Shared per-act vote-override budget (spec 2026-07-21 §2.2). One static
/// surface consumed by CardRewardVotePatch, AncientVotePatch, and the
/// streamer budget counter label. Godot-free on purpose: it rides the test
/// csproj's DecisionVotes glob, so no Godot or MegaCrit types may appear here.
/// Main-thread-only, like the tracker it wraps.
/// </summary>
internal static class VoteOverrideBudget {
    /// <summary>Override clicks only register this long after vote start, so
    /// the double-click that opened the vote can't consume an override.</summary>
    public static readonly TimeSpan ArmingDelay = TimeSpan.FromSeconds(1.5);

    private static readonly ActBudgetTracker _tracker = new();

    /// <summary>-1 unlimited, 0 disabled, >=1 per-act budget. Default 1.</summary>
    public static int Limit => BootstrapModSettings.Current?.VoteOverridesPerAct ?? 1;

    public static bool Enabled => Limit != 0;

    public static int Remaining => Limit < 0 ? int.MaxValue : Math.Max(0, Limit - _tracker.ActUsed);

    public static ActBudgetSnapshot Snapshot() => _tracker.Snapshot(Limit);

    public static BudgetResetReason Observe(string? runId, int? actIndex) =>
        _tracker.ObserveRunAndAct(runId, actIndex);

    public static void RecordUse() => _tracker.RecordUse();

    /// <summary>Pure formatter, unit-tested. Unlimited (limit &lt; 0) omits the count.</summary>
    internal static string FormatOverrideReceipt(string streamerName, string takenLabel, int limit, int remaining) {
        if (limit < 0) return $"{streamerName} overrode the vote and took {takenLabel}.";
        string noun = remaining == 1 ? "override" : "overrides";
        return $"{streamerName} overrode the vote and took {takenLabel}. {remaining} {noun} remaining this act";
    }

    internal static string FormatResetReceipt(int limit, int humanActNumber) =>
        $"Vote overrides reset to {limit} for Act {humanActNumber}";

    /// <summary>Replaces the vote's normal close receipt (TryCloseNow sends none).
    /// High priority to match the close receipt it stands in for.</summary>
    public static void SendOverrideReceipt(string takenLabel) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = FormatOverrideReceipt(
            BootstrapModSettings.GetStreamerDisplayName(), takenLabel, Limit, Remaining);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.High);
    }

    /// <summary>Mirrors the skip budget's reset receipt suppression rules:
    /// nothing for limit &lt;= 0 (off/unlimited) or unknown act.</summary>
    public static void SendResetReceiptIfAny(BudgetResetReason reason, int humanActNumber) {
        if (reason == BudgetResetReason.None) return;
        if (Limit <= 0) return;
        if (humanActNumber <= 0) return;
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        _ = coordinator.Chat.SendMessageAsync(
            FormatResetReceipt(Limit, humanActNumber), OutgoingMessagePriority.Normal);
    }

    internal static void ResetForTests() => _tracker.ResetForTests();
}

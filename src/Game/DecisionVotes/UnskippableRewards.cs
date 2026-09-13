using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 5: input restraint only, never a change to the alternatives
/// list. (1) Skip denied in the alternate-select prefix (covers the button and the
/// ESC/back hotkeys). (2) Skip button hidden and Disable()d, banner retitled, status
/// line attached. (3) The parent rewards screen gets vanilla's own
/// RewardsSet.WithSkippingDisallowed() from a _Ready PREFIX plus the header line; the
/// Proceed prefix blocks while an Unskippable card reward is still alive. Every branch
/// fails OPEN to vanilla-skippable.</summary>
internal static class UnskippableRewards {
    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, object> Screens = new();
    private static readonly object Marker = new();
    private static readonly Lazy<FieldInfo?> ExtraOptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions"));
    private static readonly Lazy<FieldInfo?> BannerField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_banner"));
    private static readonly Lazy<FieldInfo?> RewardsSetField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet"));
    private static readonly Lazy<FieldInfo?> HeaderField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_headerLabel"));
    private static readonly Lazy<FieldInfo?> RewardButtonsField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));
    private static readonly Lazy<FieldInfo?> SkipDisallowedField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_skipDisallowed"));
    private static readonly Lazy<MethodInfo?> TryEnableProceedMethod = new(() => AccessTools.Method(typeof(NRewardsScreen), "TryEnableProceedButton"));

    internal static bool IsUnskippableScreen(NCardRewardSelectionScreen screen) => Screens.TryGetValue(screen, out _);

    internal static bool ShouldDenyAlternative(NCardRewardSelectionScreen screen, int altIndex) {
        try {
            if (!IsUnskippableScreen(screen)) return false;
            var alts = ExtraOptionsField.Value?.GetValue(screen) as System.Collections.Generic.IReadOnlyList<CardRewardAlternative>;
            return alts is not null && altIndex >= 0 && altIndex < alts.Count && alts[altIndex]?.OptionId == "Skip";
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] deny check failed; failing open", ex); return false; }
    }

    /// <summary>Any alive card-reward button on the rewards screen whose reward is Unskippable.</summary>
    internal static bool HasPendingUnskippable(NRewardsScreen screen) {
        try {
            if (RewardButtonsField.Value?.GetValue(screen) is not System.Collections.Generic.IEnumerable<Control> buttons) return false;
            return buttons.Any(b => GodotObject.IsInstanceValid(b) && b is NRewardButton rb && rb.Reward is CardReward cr
                                    && RewardAuthority.Classify(cr) == AuthorityMode.Unskippable);
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] pending check failed; failing open", ex); return false; }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
    internal static class SubScreenReadyPatch {
        static void Postfix(NCardRewardSelectionScreen __instance) {
            try {
                if (RewardAuthority.ModeOfActiveReward() != AuthorityMode.Unskippable) return;
                Screens.AddOrUpdate(__instance, Marker);
                var alts = ExtraOptionsField.Value?.GetValue(__instance) as System.Collections.Generic.IReadOnlyList<CardRewardAlternative>;
                var container = __instance.GetNodeOrNull<Control>("UI/RewardAlternatives");
                if (alts is not null && container is not null) {
                    for (int i = 0; i < alts.Count && i < container.GetChildCount(); i++) {
                        if (alts[i]?.OptionId != "Skip") continue;
                        if (container.GetChild(i) is Control c) {
                            if (c is NClickableControl clickable) clickable.Disable();
                            c.Visible = false;
                        }
                    }
                }
                if (BannerField.Value?.GetValue(__instance) is NCommonBanner banner) banner.label.SetTextAutoSize("Choose a Card (no skip)");
                RemovalStatusLine.Attach(__instance, __instance.GetNodeOrNull<Control>("UI/Banner"), () => "This card reward cannot be skipped.");
                TiLog.Info("[SlayTheStreamer2][unskip] card reward screen restrained (Skip hidden and denied)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] screen restraint failed; vanilla skippable", ex); }
        }
    }

    /// <summary>PREFIX so vanilla's own DisallowSkipping branch in _Ready sees the flag.
    /// _rewardsSet is assigned in ShowScreen before the screen is pushed.</summary>
    [HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
    internal static class RewardsScreenReadyPrefix {
        static void Prefix(NRewardsScreen __instance) {
            try {
                if (!RewardAuthority.RulesActive) return;
                if (RewardsSetField.Value?.GetValue(__instance) is not RewardsSet set) return;
                if (!set.Rewards.Any(r => r is CardReward cr && RewardAuthority.Classify(cr) == AuthorityMode.Unskippable)) return;
                set.WithSkippingDisallowed();
                RewardsHeaderSubLabel.Attach(__instance, () => HeaderField.Value?.GetValue(__instance) as Control,
                    "Every card reward must be taken here.", isVisible: () => HasPendingUnskippable(__instance));
                TiLog.Info("[SlayTheStreamer2][unskip] rewards set restrained (Skip Rewards disabled)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] rewards-set restraint failed; vanilla skippable", ex); }
        }
    }

    /// <summary>Vanilla's _skipDisallowed lasts the screen's lifetime and keeps Proceed
    /// disabled while ANY reward is left, so a set with potions or gold beside the card
    /// rewards became a dead end once the cards were taken (Crystal Sphere, Surfinite
    /// 2026-09-13). Clear it, and re-run vanilla's enable check, as soon as no unskippable
    /// card reward is alive. RewardCollectedFrom fires when a reward completes;
    /// AfterOverlayShown covers the return from the sub-screen.</summary>
    private static void ReleaseIfNothingPending(NRewardsScreen screen) {
        try {
            if (SkipDisallowedField.Value?.GetValue(screen) is not true) return;
            if (HasPendingUnskippable(screen)) return;
            SkipDisallowedField.Value!.SetValue(screen, false);
            TryEnableProceedMethod.Value?.Invoke(screen, null);
            TiLog.Info("[SlayTheStreamer2][unskip] every unskippable card reward taken; Skip Rewards released");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] release failed; Proceed may stay disabled", ex); }
    }

    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.RewardCollectedFrom))]
    internal static class RewardCollectedReleasePostfix {
        static void Postfix(NRewardsScreen __instance) => ReleaseIfNothingPending(__instance);
    }

    [HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen.AfterOverlayShown))]
    internal static class AfterOverlayShownReleasePostfix {
        static void Postfix(NRewardsScreen __instance) => ReleaseIfNothingPending(__instance);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: assigns pool names to enemy creature nodes and lays out the
/// name label under the intent icons (which shift up IntentShiftPx to make
/// room — StS1 used 42px). Assignment is per NCreature node lifetime via
/// ConditionalWeakTable; death/teardown cleans up through Godot's scene tree
/// (label is a child of the creature node). Pure cosmetics: every path is
/// try/catch → Warn; no game-state contact anywhere.
/// </summary>
internal static class VoterNamesPatch {
    internal const float IntentShiftPx = 40f;
    internal const float NameGapPx = 6f;   // gap between shifted icons and the label

    private sealed class Assignment {
        public required string VoterKey;
        public required string DecoratedName;
        public VoterNameLabel? Label;
    }

    private static readonly ConditionalWeakTable<NCreature, Assignment> Assignments = new();

    private static bool FeatureOn => ModSettings.Current?.NameEnemiesAfterVoters == true;

    private static bool IsMultiplayer() {
        try {
            return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count is int n && n > 1;
        } catch { return false; }
    }

    /// <summary>Task 8's sweep: all currently named, alive, valid creature nodes.</summary>
    internal static IEnumerable<(NCreature Node, string VoterKey)> NamedLivingCreatures() {
        foreach (var (node, assignment) in Assignments) {
            if (!GodotObject.IsInstanceValid(node)) continue;
            if (node.Entity is null || node.Entity.IsDead) continue;
            yield return (node, assignment.VoterKey);
        }
    }

    // UpdateBounds(Node) is the single site that lays IntentContainer out from
    // the per-creature IntentPos marker. Postfix: draw/attach the name and
    // shift the icons up. Re-runs whenever vanilla re-lays-out — that also
    // makes mid-run setting toggles self-heal (label removed / re-added on the
    // next pass) with no restore bookkeeping: vanilla recomputes Position from
    // the marker at every call before our shift.
    [HarmonyPatch(typeof(NCreature), "UpdateBounds", typeof(Node))]
    internal static class UpdateBounds_Postfix {
        static bool Prepare(System.Reflection.MethodBase? original) {
            if (original is not null) return true;
            if (AccessTools.Method(typeof(NCreature), "UpdateBounds", new[] { typeof(Node) }) is null) {
                TiLog.Error("[SlayTheStreamer2][voter-names] NCreature.UpdateBounds(Node) not found; enemy naming disabled");
                return false;
            }
            return true;
        }

        static void Postfix(NCreature __instance) {
            try {
                if (!GodotObject.IsInstanceValid(__instance)) return;
                var existing = Assignments.TryGetValue(__instance, out var assignment) ? assignment : null;

                if (!FeatureOn) {
                    // Toggled off mid-run: drop the label; vanilla layout already restored
                    // (this postfix simply didn't shift anything this pass).
                    if (existing?.Label is { } stale && GodotObject.IsInstanceValid(stale)) {
                        stale.QueueFree();
                        existing.Label = null;
                    }
                    return;
                }
                if (IsMultiplayer()) return;
                if (NCombatRoom.Instance is null) return;              // bestiary/menus: never name
                if (__instance.Entity is null) return;                  // too early; retry next pass
                if (__instance.Entity.Monster is null) return;          // players are never named

                if (existing is null) {
                    if (!VoterNamePoolHook.TakeNameLocked(out var decorated, out var key)) return;   // empty pool: vanilla look
                    existing = new Assignment { VoterKey = key, DecoratedName = decorated };
                    Assignments.Add(__instance, existing);
                    TiLog.Info($"[SlayTheStreamer2][voter-names] named {__instance.Entity.Monster.Id.Entry} after '{decorated}'");
                }

                if (existing.Label is null || !GodotObject.IsInstanceValid(existing.Label)) {
                    existing.Label = VoterNameLabel.TryCreate(existing.DecoratedName, __instance);
                    if (existing.Label is null) return;
                    __instance.AddChild(existing.Label);
                }

                // Vanilla just set IntentContainer.Position from the marker; shift the
                // icons up and park the label's base in the freed space below them.
                var container = __instance.IntentContainer;
                var originalPos = container.Position;
                container.Position = originalPos - new Vector2(0f, IntentShiftPx);

                var label = existing.Label;
                label.Size = new Vector2(Math.Max(container.Size.X, 300f), 30f);
                label.SetBasePosition(new Vector2(
                    originalPos.X + container.Size.X * 0.5f - label.Size.X * 0.5f,
                    originalPos.Y + container.Size.Y - IntentShiftPx + NameGapPx));
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][voter-names] UpdateBounds postfix failed: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2;

/// <summary>
/// Registers the mod's Harmony patches per feature group and rolls a group back as a
/// unit when any of its classes fails to patch. Replaces a bare
/// <c>harmony.PatchAll(assembly)</c>, which stops at the first class that throws and
/// leaves every earlier class live: on game v0.113.0 one removed parameter left the
/// published build half-patched while the log said "behavior unmodified".
///
/// Each group gets its own Harmony instance (<c>slay_the_streamer_2.&lt;group&gt;</c>), so
/// a rollback is <c>UnpatchAll(thatId)</c> and touches nothing else. Classes are still
/// patched in the order <c>PatchAll</c> used (assembly metadata order), so patches that
/// share a target keep the relative order they have always had.
///
/// What counts as a failure is an EXCEPTION out of a class's patching. A class whose
/// <c>Prepare</c> returns false skips itself by design (a member that only exists on one
/// game branch) and is not a failure. The catalogue and the dependency closure live in
/// <see cref="PatchGroups"/>, which is unit-tested; this class is the Harmony glue.
/// </summary>
internal static class PatchRegistrar {
    internal const string HarmonyIdPrefix = "slay_the_streamer_2";

    /// <summary>Testing aid. Set to a comma-separated list of group names (as they
    /// appear in the patch summary, e.g. <c>boss-vote,chest-relics</c>) before launching
    /// the game to make those groups fail AFTER patching, which is the only way to see
    /// the rollback, the summary line and the chat notice against the real game. Unset
    /// in normal use, where it does nothing.</summary>
    internal const string FaultEnvVar = "SLAY_THE_STREAMER_FAIL_PATCH_GROUPS";

    internal sealed record Result(int TotalGroups, IReadOnlyList<MethodBase> PatchedMethods);

    private static readonly Dictionary<string, Harmony> _harmonies = new(StringComparer.Ordinal);

    internal static Result RegisterAll(Assembly assembly, Action<string> info, Action<string> warn, Action<string, Exception> error) {
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        var directFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        var faultGroups = FaultGroupsFromEnvironment(warn);

        foreach (var type in AccessTools.GetTypesFromAssembly(assembly)) {
            string? group = null;
            try {
                if (HarmonyMethodExtensions.GetFromType(type).Count == 0) continue;
                group = GroupFor(type, warn);
                seenGroups.Add(group);
                // The group is already coming down; do not add more patches to undo.
                if (directFailures.ContainsKey(group)) continue;
                if (!_harmonies.TryGetValue(group, out var harmony)) {
                    _harmonies[group] = harmony = new Harmony($"{HarmonyIdPrefix}.{group}");
                }
                harmony.CreateClassProcessor(type).Patch();
                // After the real patch, so the rollback has something to undo.
                if (faultGroups.Contains(group)) {
                    throw new InvalidOperationException($"fault injected through the {FaultEnvVar} environment variable");
                }
            } catch (Exception ex) {
                // Reading the attributes can itself throw (an attribute naming a type
                // that is missing on this game version), before the group is known.
                group ??= GroupFor(type, warn);
                seenGroups.Add(group);
                directFailures.TryAdd(group, $"{type.Name}: {Describe(ex)}");
                error($"[SlayTheStreamer2] patch class {type.FullName} failed to register; group '{group}' will be rolled back", ex);
            }
        }

        var off = PatchGroups.Closure(directFailures.Keys);
        foreach (var group in off) {
            string reason = directFailures.TryGetValue(group, out var direct)
                ? direct
                : "needs " + string.Join(" + ", PatchGroups.DependenciesOf(group).Where(off.Contains));
            int undone = RollBack(group, error);
            PatchGroups.MarkFailed(group, reason);
            warn($"[SlayTheStreamer2] patch group '{group}' is OFF: {reason} ({undone} patched method(s) rolled back); that feature behaves as vanilla");
        }

        var patched = _harmonies
            .Where(kv => !off.Contains(kv.Key))
            .SelectMany(kv => kv.Value.GetPatchedMethods())
            .Distinct()
            .ToList();
        info($"[SlayTheStreamer2] Harmony patched {patched.Count} method(s):");
        foreach (var m in patched) info($"[SlayTheStreamer2]   {m.DeclaringType?.FullName}.{m.Name}");
        return new Result(seenGroups.Count, patched);
    }

    /// <summary>Removes every patch this mod applied. For the outer init catch, so that
    /// "subsequent game behavior unmodified" is true when it is logged.</summary>
    internal static void RollBackEverything(Action<string, Exception> error) {
        foreach (var group in _harmonies.Keys.ToList()) RollBack(group, error);
    }

    private static int RollBack(string group, Action<string, Exception> error) {
        if (!_harmonies.TryGetValue(group, out var harmony)) return 0;
        try {
            int count = harmony.GetPatchedMethods().Count();
            // ALWAYS pass the id: UnpatchAll() with no argument removes every mod's patches.
            harmony.UnpatchAll(harmony.Id);
            return count;
        } catch (Exception ex) {
            error($"[SlayTheStreamer2] rolling back patch group '{group}' threw; some of its patches may still be applied", ex);
            return 0;
        }
    }

    private static HashSet<string> FaultGroupsFromEnvironment(Action<string> warn) {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        string? raw;
        try { raw = Environment.GetEnvironmentVariable(FaultEnvVar); } catch (Exception) { return groups; }
        if (string.IsNullOrWhiteSpace(raw)) return groups;
        foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) groups.Add(name);
        warn($"[SlayTheStreamer2] {FaultEnvVar} is set: patch group(s) {string.Join(", ", groups)} will be failed on purpose (testing aid)");
        return groups;
    }

    /// <summary>Group by OUTERMOST class: a class nested in a patch container belongs to
    /// the container's group. An unassigned class registers as a group of its own.</summary>
    private static string GroupFor(Type type, Action<string> warn) {
        var outermost = type;
        while (outermost.DeclaringType is not null) outermost = outermost.DeclaringType;
        var group = PatchGroups.GroupOf(outermost.Name);
        if (group is not null) return group;
        warn($"[SlayTheStreamer2] patch class {type.FullName} has no entry in PatchGroups; registering it as a group of its own");
        return "ungrouped:" + outermost.Name;
    }

    /// <summary>Harmony wraps the cause ("Patching exception in method ..."); the inner
    /// message is the useful part. One line, bounded, for the summary.</summary>
    private static string Describe(Exception ex) {
        var message = (ex.InnerException ?? ex).Message;
        int newline = message.IndexOfAny(new[] { '\r', '\n' });
        if (newline >= 0) message = message.Substring(0, newline);
        return message.Length <= 180 ? message : message.Substring(0, 177) + "...";
    }
}

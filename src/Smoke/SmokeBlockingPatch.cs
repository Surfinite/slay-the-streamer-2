// SMOKE-TEST: DELETE AFTER VALIDATION. This is the scary one — if it deadlocks,
// the game hangs at the Settings menu. Force-quit and check logs for [smoke-C] entries.
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace SlayTheStreamer2.Smoke;

[HarmonyPatch(typeof(NSettingsScreen), "_Ready")]
internal static class SmokeBlockingPatch {
    private static int _fired = 0;

    // Harmony calls Prepare twice: once class-level (original=null) and once per
    // target (original=MethodBase). Returning false on null skips the entire class.
    static bool Prepare(MethodBase original) {
        if (original is null) return true;   // class-level: allow processing
        Log.Info($"[smoke-C] Prepare: target resolved as {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static void Prefix() {
        if (Interlocked.Exchange(ref _fired, 1) == 1) return;
        Log.Info("[smoke-C] NSettingsScreen._Ready prefix fired (BLOCKING)");
        SmokeRunner.RunSmokeCBlocking(ModEntry.SmokeChat);
    }
}

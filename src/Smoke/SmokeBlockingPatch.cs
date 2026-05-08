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

    static bool Prepare(MethodBase original) {
        if (original is null) {
            Log.Warn("[smoke-C] Prepare: target NSettingsScreen._Ready not found; smoke disabled.");
            return false;
        }
        Log.Info($"[smoke-C] Prepare: target resolved as {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static void Prefix() {
        if (Interlocked.Exchange(ref _fired, 1) == 1) return;
        Log.Info("[smoke-C] NSettingsScreen._Ready prefix fired (BLOCKING)");
        SmokeRunner.RunSmokeCBlocking(ModEntry.SmokeChat);
    }
}

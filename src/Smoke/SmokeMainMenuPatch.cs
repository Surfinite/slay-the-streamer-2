// SMOKE-TEST: DELETE AFTER VALIDATION.
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SlayTheStreamer2.Smoke;

[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class SmokeMainMenuPatch {
    private static int _fired = 0;

    /// <summary>Validates Harmony picked up the patch attribute and resolved the target.</summary>
    static bool Prepare(MethodBase original) {
        if (original is null) {
            Log.Warn("[smoke-B] Prepare: target NMainMenu._Ready not found; smoke disabled.");
            return false;
        }
        Log.Info($"[smoke-B] Prepare: target resolved as {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static void Prefix() {
        if (Interlocked.Exchange(ref _fired, 1) == 1) return;
        Log.Info("[smoke-B] NMainMenu._Ready prefix fired (fire-and-forget)");
        _ = SmokeRunner.RunSmokeB(ModEntry.SmokeChat);
    }
}

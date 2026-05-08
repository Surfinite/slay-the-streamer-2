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

    /// <summary>
    /// Harmony calls Prepare twice: once at class-level with original=null
    /// ("should I process this patch class?"), once per resolved target. Returning
    /// false on the class-level call kills the entire patch class before Harmony
    /// even tries to resolve _Ready — so we must return true for the null case.
    /// On the second call, log the resolved target so we can confirm patch binding.
    /// </summary>
    static bool Prepare(MethodBase original) {
        if (original is null) return true;   // class-level: allow processing
        Log.Info($"[smoke-B] Prepare: target resolved as {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static void Prefix() {
        if (Interlocked.Exchange(ref _fired, 1) == 1) return;
        Log.Info("[smoke-B] NMainMenu._Ready prefix fired (fire-and-forget)");
        _ = SmokeRunner.RunSmokeB(ModEntry.SmokeChat);
    }
}

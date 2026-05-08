using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using SlayTheStreamer2.Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2;

[ModInitializer("Init")]
public static class ModEntry {
    internal static int GodotMainThreadId;

    public static void Init() {
        try {
            GodotMainThreadId = System.Environment.CurrentManagedThreadId;
            Log.Info($"[slay_the_streamer_2] mod loading... (init thread={GodotMainThreadId})");

            // Godot version + main loop type for cross-version troubleshooting.
            var godotVer = Engine.GetVersionInfo();
            Log.Info($"[slay_the_streamer_2] Godot {godotVer["string"]}, " +
                $"main loop type: {Engine.GetMainLoop()?.GetType().Name ?? "<null>"}");
            Log.Info($"[slay_the_streamer_2] log file location: " +
                $"{System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)}" +
                $"/Godot/app_userdata/Slay the Spire 2/logs/");

            // 1. Resolve SceneTree once with explicit cast and null check.
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null) {
                Log.Error("[slay_the_streamer_2] Engine.GetMainLoop() is not a SceneTree " +
                    "or has no Root — main loop not initialized at [ModInitializer] time. " +
                    "Aborting mod load.");
                return;
            }

            // 2. Attach dispatcher node (primary mechanism).
            var autoload = new DispatcherAutoload { Name = "DispatcherAutoload" };
            tree.Root.AddChild(autoload);
            Log.Info("[slay_the_streamer_2] dispatcher node added to SceneTree.Root");

            // 3. Optional instrumentation: register as engine singleton.
            try {
                Engine.RegisterSingleton("DispatcherAutoload", autoload);
                Log.Info("[slay_the_streamer_2] dispatcher also registered with Engine.RegisterSingleton");
            } catch (Exception e) {
                Log.Warn($"[slay_the_streamer_2] Engine.RegisterSingleton failed (continuing): {e.Message}");
            }

            // 4. Wire IMainThreadDispatcher.
            var dispatcher = new GodotMainThreadDispatcher();
            dispatcher.SetAutoload(autoload);

            // 5. Plan A logging passthrough (verified thread-safe in notes/03).
            TiLog.Sink = (level, msg, ex) => {
                switch (level) {
                    case SlayTheStreamer2.Ti.Internal.LogLevel.Error: Log.Error(ex is null ? msg : $"{msg} :: {ex}"); break;
                    case SlayTheStreamer2.Ti.Internal.LogLevel.Warn:  Log.Warn(msg); break;
                    default:             Log.Info(msg); break;
                }
            };

            // 6. (Smoke wiring will be added in Task 4.1; intentionally absent here so
            //     this snapshot of ModEntry can serve as a clean Plan-B-ready skeleton.)

            // 7. Apply Harmony patches with diagnostic logging.
            var harmony = new Harmony("slay_the_streamer_2");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            var patchedMethods = harmony.GetPatchedMethods().ToList();
            Log.Info($"[slay_the_streamer_2] Harmony patched {patchedMethods.Count} method(s):");
            foreach (var m in patchedMethods) {
                Log.Info($"[slay_the_streamer_2]   {m.DeclaringType?.FullName}.{m.Name}");
            }
            if (patchedMethods.Count == 0) {
                Log.Warn("[slay_the_streamer_2] WARNING: 0 patches applied. Check that " +
                    "[HarmonyPatch] attributes target valid methods.");
            }

            // 8. Sanity check: Voter.Default should be set (will be in Task 4.1).
            //    For now (no smoke wiring), this is intentionally not yet checked here.

            Log.Info("[slay_the_streamer_2] Init complete.");
        } catch (Exception e) {
            // Bound the blast radius: half-loaded mod is worse than not-loaded mod.
            Log.Error($"[slay_the_streamer_2] FATAL: Init failed; subsequent game " +
                $"behavior unmodified. Exception: {e}");
        }
    }
}

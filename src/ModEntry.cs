using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using SlayTheStreamer2.Godot;
using SlayTheStreamer2.Ti.Internal;

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
                $"/SlayTheSpire2/logs/godot.log");

            // 1. Resolve SceneTree once with explicit cast and null check.
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null) {
                Log.Error("[slay_the_streamer_2] Engine.GetMainLoop() is not a SceneTree " +
                    "or has no Root — main loop not initialized at [ModInitializer] time. " +
                    "Aborting mod load.");
                return;
            }

            // 2. Attach dispatcher node via deferred add_child (root is busy during
            //    NGame._EnterTree when [ModInitializer] runs; direct AddChild errors).
            var autoload = new DispatcherAutoload { Name = "DispatcherAutoload" };
            tree.Root.CallDeferred("add_child", autoload);
            Log.Info("[slay_the_streamer_2] dispatcher node deferred-attach queued (CallDeferred add_child)");

            // 3. Optional instrumentation: register as engine singleton.
            try {
                Engine.RegisterSingleton("DispatcherAutoload", autoload);
                Log.Info("[slay_the_streamer_2] dispatcher also registered with Engine.RegisterSingleton");
            } catch (Exception e) {
                Log.Warn($"[slay_the_streamer_2] Engine.RegisterSingleton failed (continuing): {e.Message}");
            }

            // 4. Wire IMainThreadDispatcher.
            //    Plan B Phase 1 will hold this dispatcher reference (currently a local;
            //    promote to a static field when wiring TwitchIrcChatService + Voter.Default).
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

            // 6. Apply Harmony patches with diagnostic logging.
            //    Plan B will add real patches against game decision call-sites; with
            //    the smoke removed, this currently logs "0 patches applied".
            var harmony = new Harmony("slay_the_streamer_2");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            var patchedMethods = harmony.GetPatchedMethods().ToList();
            Log.Info($"[slay_the_streamer_2] Harmony patched {patchedMethods.Count} method(s):");
            foreach (var m in patchedMethods) {
                Log.Info($"[slay_the_streamer_2]   {m.DeclaringType?.FullName}.{m.Name}");
            }

            Log.Info("[slay_the_streamer_2] Init complete (skeleton — Plan B will fill in vote wiring).");
        } catch (Exception e) {
            // Bound the blast radius: half-loaded mod is worse than not-loaded mod.
            Log.Error($"[slay_the_streamer_2] FATAL: Init failed; subsequent game " +
                $"behavior unmodified. Exception: {e}");
        }
    }
}

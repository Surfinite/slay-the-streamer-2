using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using SlayTheStreamer2.Godot;
using SlayTheStreamer2.Smoke;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2;

[ModInitializer("Init")]
public static class ModEntry {
    internal static int GodotMainThreadId;

    // SMOKE-TEST: DELETE AFTER VALIDATION.
    internal static FakeChatService SmokeChat = null!;
    internal static Task SmokeATask = Task.CompletedTask;

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

            // 2. Attach dispatcher node (primary mechanism).
            // Use CallDeferred("add_child", ...) instead of direct AddChild because
            // [ModInitializer] runs during NGame._EnterTree, when the root is busy
            // building the scene tree — direct AddChild errors out with "Parent node
            // is busy setting up children." Deferring queues the attach for the next
            // idle frame, by which time the tree is ready.
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

            // 6. Smoke wiring (DISPOSABLE — delete after smoke success).
            // SMOKE-ONLY: FakeChatService.ConnectAsync completes synchronously.
            // Do NOT copy this .GetAwaiter().GetResult() pattern to TwitchIrcChatService —
            // sync-over-async on the main thread is the exact deadlock source we're trying
            // to rule out. Plan B must use the connection-state callback instead.
            var clock = new SystemClock();
            var scheduler = new SystemTimerScheduler();
            SmokeChat = new FakeChatService();
            SmokeChat.ConnectAsync("smoke", new ChatCredentials("smokebot", "abc"))
                     .GetAwaiter().GetResult();
            var coord = new VoteCoordinator(SmokeChat, clock, scheduler, dispatcher);
            Voter.Default = coord;

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

            // 8. Sanity check before declaring success.
            if (Voter.Default is null) {
                Log.Error("[slay_the_streamer_2] FATAL: Voter.Default is null after wiring; " +
                    "smoke cannot run. Aborting.");
                return;
            }

            // 9. Smoke A: fire-and-forget vote from this startup context (DISPOSABLE).
            SmokeATask = SmokeRunner.RunSmokeA(SmokeChat);

            Log.Info("[slay_the_streamer_2] Init complete.");
        } catch (Exception e) {
            // Bound the blast radius: half-loaded mod is worse than not-loaded mod.
            Log.Error($"[slay_the_streamer_2] FATAL: Init failed; subsequent game " +
                $"behavior unmodified. Exception: {e}");
        }
    }
}

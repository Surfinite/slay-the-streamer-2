using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using SlayTheStreamer2.Game.Bootstrap;
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;
using SlayTheStreamer2.Godot;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2;

[ModInitializer("Init")]
public static class ModEntry {
    internal static int GodotMainThreadId;

    private static readonly CancellationTokenSource _modCts = new();
    internal static TwitchIrcChatService? Chat { get; private set; }
    internal static YouTubeChatService? YouTube { get; private set; }
    internal static YouTubeHttp? YouTubeHttp { get; private set; }
    internal static MultiChatService? Multi { get; private set; }
    internal static VoteCoordinator? Coordinator { get; private set; }
    internal static SettingsResult? Settings { get; private set; }
    internal static string? ModVersion { get; private set; }

    public static void Init() {
        try {
            GodotMainThreadId = System.Environment.CurrentManagedThreadId;
            Log.Info($"[SlayTheStreamer2] mod loading... (init thread={GodotMainThreadId})");

            // Godot version + main loop type for cross-version troubleshooting.
            var godotVer = Engine.GetVersionInfo();
            Log.Info($"[SlayTheStreamer2] Godot {godotVer["string"]}, " +
                $"main loop type: {Engine.GetMainLoop()?.GetType().Name ?? "<null>"}");
            Log.Info($"[SlayTheStreamer2] log file location: " +
                $"{System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)}" +
                $"/SlayTheSpire2/logs/godot.log");

            // 1. Resolve SceneTree once with explicit cast and null check.
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null) {
                Log.Error("[SlayTheStreamer2] Engine.GetMainLoop() is not a SceneTree " +
                    "or has no Root — main loop not initialized at [ModInitializer] time. " +
                    "Aborting mod load.");
                return;
            }

            // 2. Attach dispatcher node via deferred add_child (root is busy during
            //    NGame._EnterTree when [ModInitializer] runs; direct AddChild errors).
            var autoload = new DispatcherAutoload { Name = "DispatcherAutoload" };
            tree.Root.CallDeferred("add_child", autoload);
            Log.Info("[SlayTheStreamer2] dispatcher node deferred-attach queued (CallDeferred add_child)");

            // 3. Optional instrumentation: register as engine singleton.
            try {
                Engine.RegisterSingleton("DispatcherAutoload", autoload);
                Log.Info("[SlayTheStreamer2] dispatcher also registered with Engine.RegisterSingleton");
            } catch (Exception e) {
                Log.Warn($"[SlayTheStreamer2] Engine.RegisterSingleton failed (continuing): {e.Message}");
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

            // 6. Resolve settings file path Godot-side, load settings.
            var modVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            ModVersion = modVersion;
            Log.Info($"[SlayTheStreamer2] mod version: {modVersion}");

            var settingsPath = Path.Combine(OS.GetUserDataDir(), "slay_the_streamer_2.json");
            var settingsResult = BootstrapModSettings.Load(settingsPath);
            Settings = settingsResult;
            ChatSettings? settings = null;
            switch (settingsResult) {
                case SettingsResult.Success s:
                    settings = s.Settings;
                    Log.Info($"[SlayTheStreamer2] settings loaded; channel=#{settings.Channel}");
                    foreach (var w in s.Warnings) Log.Info($"[SlayTheStreamer2]   {w}");
                    break;
                case SettingsResult.Missing m:
                    Log.Info($"[SlayTheStreamer2] no settings file at {m.Path}; mod loaded but Twitch not connected. " +
                             "Create the file with: { \"schemaVersion\": 1, \"channel\": \"...\", \"username\": \"...\", \"oauthToken\": \"oauth:...\" }");
                    break;
                case SettingsResult.Malformed m:
                    Log.Error($"[SlayTheStreamer2] settings file at {m.Path} is malformed: {m.Reason}. Mod loaded but not connecting.");
                    break;
            }

            // 7. Build TI services if settings loaded.
            var clock = new SlayTheStreamer2.Ti.Internal.SystemClock();
            var scheduler = new SlayTheStreamer2.Ti.Internal.SystemTimerScheduler();

            if (settings is not null) {
                // Construct Twitch (unchanged from B.1).
                Chat = new TwitchIrcChatService(
                    dispatcher: dispatcher,
                    clock: clock,
                    scheduler: scheduler,
                    sendCapacity: 20,
                    sendWindow: TimeSpan.FromSeconds(30));
                _ = Chat.ConnectAsync(settings.Channel, settings.Credentials, _modCts.Token);

                // Optionally construct YouTube (per D6: only when settings.YoutubeChannelId
                // is non-null; ModSettings already trims/validates).
                YouTubeChatService? youtube = null;
                YouTubeHttp? youtubeHttp = null;
                if (!string.IsNullOrEmpty(settings.YoutubeChannelId)) {
                    youtubeHttp = new YouTubeHttp();
                    var discovery = new YouTubeLiveBroadcastDiscovery(youtubeHttp);
                    var scraper = new YouTubeLiveChatScraper(youtubeHttp);
                    youtube = new YouTubeChatService(
                        dispatcher: dispatcher,
                        clock: clock,
                        scheduler: scheduler,
                        discovery: discovery,
                        scraper: scraper);
                    _ = youtube.ConnectAsync(settings.YoutubeChannelId);
                }
                YouTube = youtube;
                YouTubeHttp = youtubeHttp;

                // Build MultiChatService — always wrap, even Twitch-only.
                var multi = youtube is null
                    ? new MultiChatService((ChatPlatformNames.Twitch, (IChatConsumer)Chat))
                    : new MultiChatService(
                        (ChatPlatformNames.Twitch, (IChatConsumer)Chat),
                        (ChatPlatformNames.YouTube, (IChatConsumer)youtube));
                Multi = multi;

                var configuredPlatforms = youtube is null
                    ? new[] { ChatPlatformNames.Twitch }
                    : new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube };

                Coordinator = new VoteCoordinator(
                    multi,
                    configuredPlatforms,
                    clock,
                    scheduler,
                    dispatcher);
                Voter.Default = Coordinator;
            }

            // 8. Apply Harmony patches with diagnostic logging.
            //    NeowBlessingVotePatch attaches here via PatchAll.
            var harmony = new Harmony("slay_the_streamer_2");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            var patchedMethods = harmony.GetPatchedMethods().ToList();
            Log.Info($"[SlayTheStreamer2] Harmony patched {patchedMethods.Count} method(s):");
            foreach (var m in patchedMethods) {
                Log.Info($"[SlayTheStreamer2]   {m.DeclaringType?.FullName}.{m.Name}");
            }

            Log.Info("[SlayTheStreamer2] Init complete.");
        } catch (Exception e) {
            // Bound the blast radius: half-loaded mod is worse than not-loaded mod.
            Log.Error($"[SlayTheStreamer2] FATAL: Init failed; subsequent game " +
                $"behavior unmodified. Exception: {e}");
        }
    }
}

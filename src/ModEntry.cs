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

    // D8 startup + state-change receipt bookkeeping (Task 27).
    private static readonly TimeSpan ReceiptDebounce = TimeSpan.FromSeconds(120);
    private static DateTimeOffset _lastYouTubeReceiptAt = DateTimeOffset.MinValue;
    private static bool _twitchStartupReceiptSent;
    private static ChatConnectionState _lastYouTubeStateForReceipt = ChatConnectionState.Disconnected;

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

                // D8 receipts: startup (one-shot on Twitch connect) + YT state-change
                // (120s debounce). Twitch is the only platform receipts are sent on (D3).
                var twitchForReceipts = Chat;
                var youtubeForReceipts = youtube;
                multi.ChildConnectionStateChanged += (_, e) =>
                    OnChildConnectionStateChanged(twitchForReceipts, youtubeForReceipts, e);
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

    /// <summary>
    /// Wired to MultiChatService.ChildConnectionStateChanged. Sends two kinds of D8
    /// receipts on Twitch:
    ///   - Startup receipt: one-shot on Twitch's first ConnectedReadWrite transition.
    ///     Extends B.1's existing startup receipt with YT status when YT is configured.
    ///   - YouTube state-change receipt: mid-session, gated by 120s debounce so a
    ///     flapping YT connection doesn't spam Twitch chat.
    /// </summary>
    private static void OnChildConnectionStateChanged(
        TwitchIrcChatService twitch,
        YouTubeChatService? youtube,
        ChildConnectionStateChangedEventArgs e) {
        try {
            // Twitch first-time-connected receipt (startup).
            if (e.ChildName == ChatPlatformNames.Twitch &&
                e.Inner.NewState == ChatConnectionState.ConnectedReadWrite &&
                !_twitchStartupReceiptSent) {
                _twitchStartupReceiptSent = true;
                var msg = BuildStartupReceipt(youtube);
                _ = twitch.SendMessageAsync(msg, OutgoingMessagePriority.High);
            }

            // YouTube state-change receipts (mid-session) — gated by 120s debounce.
            if (e.ChildName == ChatPlatformNames.YouTube && youtube is not null) {
                var now = DateTimeOffset.UtcNow;
                var stateChangedFromLast = e.Inner.NewState != _lastYouTubeStateForReceipt;
                var inDebounce = (now - _lastYouTubeReceiptAt) < ReceiptDebounce;

                if (!stateChangedFromLast || inDebounce) return;

                var receipt = BuildYouTubeStateReceipt(youtube, e.Inner.NewState);
                if (receipt is not null) {
                    _ = twitch.SendMessageAsync(receipt, OutgoingMessagePriority.Normal);
                    _lastYouTubeReceiptAt = now;
                    _lastYouTubeStateForReceipt = e.Inner.NewState;
                }
            }
        } catch (Exception ex) {
            TiLog.Error("[ModEntry] OnChildConnectionStateChanged handler threw", ex);
        }
    }

    private static string BuildStartupReceipt(YouTubeChatService? youtube) {
        // Per Task 27 spec: exact wording — receipt-text strings are used by
        // operator-validation Step 7 verification, so they MUST match.
        if (youtube is null)
            return "slay-the-streamer-2 connected (Twitch).";
        return youtube.State switch {
            ChatConnectionState.ConnectedReadOnly =>
                "slay-the-streamer-2 connected (Twitch & YouTube tracking).",
            _ =>
                "slay-the-streamer-2 connected (Twitch). YouTube: no live broadcast found, retrying.",
        };
    }

    private static string? BuildYouTubeStateReceipt(YouTubeChatService youtube, ChatConnectionState newState) {
        return newState switch {
            ChatConnectionState.ConnectedReadOnly =>
                "YouTube connected: tracking chat.",
            ChatConnectionState.Reconnecting => youtube.LastStatusReason switch {
                YouTubeChatStatusReason.NoLiveBroadcastFound =>
                    "YouTube: no live broadcast found, retrying.",
                YouTubeChatStatusReason.LiveBroadcastEnded =>
                    "YouTube: live broadcast ended; will resume when next broadcast starts.",
                YouTubeChatStatusReason.NetworkError =>
                    "YouTube: connection lost; will retry.",
                YouTubeChatStatusReason.RateLimited =>
                    "YouTube: temporarily rate-limited; will retry.",
                YouTubeChatStatusReason.ScraperParseFailed =>
                    "YouTube: connection issue; will retry.",
                _ => "YouTube disconnected; will retry every ~60s.",
            },
            _ => null,
        };
    }
}

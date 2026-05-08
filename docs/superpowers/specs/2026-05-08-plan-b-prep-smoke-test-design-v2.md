# Plan B prep — smoke test design (v2)

**Date:** 2026-05-08
**Status:** revised after 6-reviewer meta-review (see `META-REVIEW-2026-05-08-plan-b-prep-smoke-test-design.md`)
**Supersedes:** `2026-05-08-plan-b-prep-smoke-test-design.md`

## Purpose

Validate two architectural concerns from `notes/06-followups-and-deferred.md` *before* writing Plan B's full implementation:

1. **#6 Godot autoload from a mod assembly.** Can a mod register a `Node`-derived class to receive frame ticks at runtime? `AddAutoloadSingleton` requires a class reference resolvable at engine boot, which a runtime-loaded mod assembly can't provide. The candidate runtime mechanism is `Engine.GetMainLoop() as SceneTree → Root.AddChild(node)`, optionally also `Engine.RegisterSingleton(name, instance)`.
2. **#7 Harmony deadlock risk.** Can a Harmony prefix safely `await Voter.Start(...).AwaitWinnerAsync()` on the Godot main thread when the underlying timer dispatches its close callback back through the same dispatcher? Plan A's `RunContinuationsAsynchronously` design *should* prevent this, but the failure mode (main-thread `.Result`/`.Wait()` blocking the dispatcher's pump) is real and needs to be tested with a realistic blocking pattern, not just a fire-and-forget pattern.

## Scope

Three smoke vote scenarios using `FakeChatService` (in-memory, no network, no auth):

- **Smoke A** — fired from `[ModInitializer("Init")]`. Exercises the dispatcher mechanics and `AwaitWinnerAsync` from a startup context. Fire-and-forget pattern.
- **Smoke B** — fired from a `[HarmonyPatch(typeof(NMainMenu), "_Ready")]` prefix. Exercises Harmony patch discovery and fire-and-forget async vote startup from a main-thread prefix. <!-- CHANGED: scope reframed — does NOT validate blocking. Smoke C does that. — Reviewers: all 6 -->
- **Smoke C** — fired from a `[HarmonyPatch(typeof(NSettingsScreen), "_Ready")]` prefix that **blocks** on `AwaitWinnerAsync().GetAwaiter().GetResult()`. Exercises the realistic Plan-B pattern: a Harmony prefix that synchronously waits for a vote winner so it can substitute the player's input. **This is the architectural validation Plan B actually depends on.** <!-- CHANGED: added entire Smoke C — Reviewers: all 6 -->

All three vote scenarios:
- 3-second duration, 3 options (`["A","B","C"]`), no policy overrides.
- Inject `"#0"` once via `FakeChatService.Inject(ChatMessage)` so the expected winner is index `0` (deterministic). <!-- CHANGED: SimulateMessage→Inject — Reviewers: 4/6 -->
- Log `"[smoke-X] starting"` at vote start and `"[smoke-X] winner=N (expected 0)"` at vote end via `TiLog.Sink` → `MegaCrit.Sts2.Core.Logging.Log.Info`.

Smokes B and C **wait for Smoke A's task to complete** before starting their own vote, to avoid the `VoteCoordinator` single-session race condition. <!-- CHANGED: serialization — Reviewers: DeepSeek, GPT5.5; validated against Plan A's Start_WhileOpen_Throws test -->

## Out of scope

- Real Twitch IRC connection (`TwitchIrcChatService` — Plan B Phase 1).
- Player-input substitution patterns (the actual vote-replaces-click) — Plan B core hooks. Smoke C validates the *blocking pattern* but does not actually substitute any player input.
- Card / event / map / shop / boss / Neow Harmony patches — Plan B Phase 2+.
- UI prompts, sound, anything visible to the streamer beyond log lines.
- Configurable vote labels / durations.

## Disposable vs permanent

**Permanent (Plan B retains as Phase-1 scaffolding):**
- `src/slay_the_streamer_2.csproj` extension (0Harmony.dll reference).
- `src/0Harmony.dll` (refreshed by build.ps1 alongside sts2.dll).
- `src/ModEntry.cs` (real `[ModInitializer]` implementation).
- `src/Godot/DispatcherAutoload.cs`.
- `src/Godot/GodotMainThreadDispatcher.cs` (with full `IMainThreadDispatcher` interface implementation).
- `build.ps1` extension and new `install.ps1` / `uninstall.ps1`.

**Disposable (delete after smoke success):**
- `src/Smoke/SmokeRunner.cs`.
- `src/Smoke/SmokeMainMenuPatch.cs`.
- `src/Smoke/SmokeBlockingPatch.cs`.
- The smoke launcher block in `ModEntry.Init()`.
- The static `ModEntry.SmokeChat` and `ModEntry.SmokeATask` references.

## File layout

```
src/
├── slay_the_streamer_2.csproj    EXTEND  +0Harmony.dll reference
├── slay_the_streamer_2.json      KEEP    (manifest, already correct)
├── project.godot                 KEEP
├── icon.svg                      KEEP
├── sts2.dll                      auto    (refreshed by build.ps1)
├── 0Harmony.dll                  NEW     (refreshed by build.ps1 from game)
├── ModEntry.cs                   REPLACE [ModInitializer("Init")] real impl
├── Ti/                           KEEP    (Plan A — unchanged)
├── Godot/                        NEW
│   ├── DispatcherAutoload.cs           Node : exposes Post(Action) via CallDeferred
│   └── GodotMainThreadDispatcher.cs    IMainThreadDispatcher impl, Post + DrainAsync
└── Smoke/                        NEW DISPOSABLE
    ├── SmokeRunner.cs                  RunSmokeA / RunSmokeB / RunSmokeC
    ├── SmokeMainMenuPatch.cs           [HarmonyPatch] on NMainMenu._Ready (fire-and-forget)
    └── SmokeBlockingPatch.cs           [HarmonyPatch] on NSettingsScreen._Ready (blocking)

build.ps1                         EXTEND  refresh 0Harmony.dll, dotnet publish, assemble dist/
install.ps1                       NEW     copy dist/slay_the_streamer_2/ → game's mods/
uninstall.ps1                     NEW     remove from game's mods/                        <!-- CHANGED: added — Reviewer: Opus -->
dist/                             gitignored
```

## Component contracts

### `DispatcherAutoload : Node` (Godot-side)

```csharp
// Despite the name, this is a runtime-attached Node, not a Godot project autoload
// configured via ProjectSettings. The class name preserves the connection to
// notes/06 item #6, which tracks the autoload-from-a-mod question.
public partial class DispatcherAutoload : Node {
    public void Post(Action action) {
        ArgumentNullException.ThrowIfNull(action);
        // Use string literal "Run" instead of MethodName.Run for portability across
        // Godot source-generator versions. Both forms work in 4.5.1 but the literal
        // is robust to source-gen behavior changes.                                       // CHANGED: string literal — Reviewers: 3 — defense-in-depth
        CallDeferred("Run", Callable.From((Action)action));                                // CHANGED: explicit Action cast for overload resolution — GPT5.5
    }

    private void Run(Callable callable) {
        try {
            callable.Call();
        } catch (Exception e) {
            // Catch and log so deferred-call failures don't bubble into Godot's loop.
            MegaCrit.Sts2.Core.Logging.Log.Error(
                $"[slay_the_streamer_2] DispatcherAutoload.Run threw: {e}");                // CHANGED: catch + log — GPT5.5
        }
    }
}
```

### `GodotMainThreadDispatcher : IMainThreadDispatcher`

```csharp
public sealed class GodotMainThreadDispatcher : IMainThreadDispatcher {
    private DispatcherAutoload? _autoload;

    public void SetAutoload(DispatcherAutoload a) => _autoload = a;

    public void Post(Action action) =>
        (_autoload ?? throw new InvalidOperationException(
            "GodotMainThreadDispatcher.Post called before SetAutoload."))
            .Post(action);

    /// <summary>
    /// Barrier-Post: completes after previously-posted actions have run.
    /// Does NOT recursively drain actions posted during the drain (Plan A
    /// production code does not call DrainAsync, verified by grep).
    /// </summary>
    public Task DrainAsync() {                                                              // CHANGED: implement DrainAsync — Reviewers: all 6
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
```

### `ModEntry.Init()` (the `[ModInitializer]`)

```csharp
[ModInitializer("Init")]
public static class ModEntry {
    // SMOKE-TEST: DELETE AFTER VALIDATION.                                                // CHANGED: smoke-test header — 4 reviewers
    internal static FakeChatService SmokeChat = null!;
    internal static Task SmokeATask = Task.CompletedTask;
    internal static int GodotMainThreadId;                                                  // CHANGED: thread-ID — GPT5.5

    public static void Init() {
        try {                                                                                // CHANGED: top-level try/catch — Opus
            GodotMainThreadId = Environment.CurrentManagedThreadId;
            Log.Info($"[slay_the_streamer_2] mod loading... (init thread={GodotMainThreadId})");
            Log.Info($"[slay_the_streamer_2] log file location: " +
                $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}" +
                $"/Godot/app_userdata/Slay the Spire 2/logs/");                              // CHANGED: log path hint — DeepSeek

            // 1. Resolve SceneTree once with explicit cast and null check.                  // CHANGED: SceneTree cast + cached + null-checked — GPT5.5 (real compile bug + 5/6 fallback bug)
            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree?.Root is null) {
                Log.Error("[slay_the_streamer_2] Engine.GetMainLoop() is not a SceneTree " +
                    "or has no Root — main loop not initialized at [ModInitializer] time. " +
                    "Aborting mod load.");
                return;
            }

            // 2. Attach dispatcher node (primary mechanism: AddChild).                      // CHANGED: AddChild primary, RegisterSingleton optional — GPT5.5, Opus
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
                    case LogLevel.Error: Log.Error(ex is null ? msg : $"{msg} :: {ex}"); break;
                    case LogLevel.Warn:  Log.Warn(msg); break;
                    default:             Log.Info(msg); break;
                }
            };

            // 6. Smoke wiring (DISPOSABLE).
            // SMOKE-ONLY: FakeChatService.ConnectAsync completes synchronously.            // CHANGED: smoke-only comment — 3+ reviewers
            // Do NOT copy this .GetAwaiter().GetResult() pattern to TwitchIrcChatService —
            // sync-over-async on the main thread is the exact deadlock source we're trying
            // to rule out. Plan B must use the connection-state callback instead.
            var clock = new SystemClock();
            var scheduler = new SystemTimerScheduler(clock);
            SmokeChat = new FakeChatService();
            SmokeChat.ConnectAsync("smoke", new ChatCredentials("smokebot", "abc"))
                     .GetAwaiter().GetResult();
            var coord = new VoteCoordinator(SmokeChat, clock, scheduler, dispatcher);
            Voter.Default = coord;

            // 7. Apply Harmony patches with diagnostic logging.                            // CHANGED: patch logging — 5/6 reviewers
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

            // 8. Smoke A: fire-and-forget vote from this startup context (DISPOSABLE).
            SmokeATask = SmokeRunner.RunSmokeA(SmokeChat);

            Log.Info("[slay_the_streamer_2] Init complete.");
        } catch (Exception e) {
            // Bound the blast radius: half-loaded mod is worse than not-loaded mod.
            Log.Error($"[slay_the_streamer_2] FATAL: Init failed; subsequent game " +
                $"behavior unmodified. Exception: {e}");
        }
    }
}
```

### `SmokeRunner` (disposable)

```csharp
// SMOKE-TEST: DELETE AFTER VALIDATION.                                                    // CHANGED: header — 4 reviewers
public static class SmokeRunner {
    public static async Task RunSmokeA(FakeChatService chat) =>
        await Run(chat, "smoke-A", "alice", "alice-id", waitForPrior: false);

    public static async Task RunSmokeB(FakeChatService chat) =>
        await Run(chat, "smoke-B", "bob", "bob-id", waitForPrior: true);                   // CHANGED: serialize after Smoke A — DeepSeek, GPT5.5

    /// <summary>Smoke C: blocking-await variant. Validates Plan B's realistic pattern.</summary>
    public static int RunSmokeCBlocking(FakeChatService chat) {                            // CHANGED: NEW Smoke C — all 6 reviewers
        try {
            // Wait for prior smokes to complete (avoid VoteCoordinator single-session race).
            ModEntry.SmokeATask.GetAwaiter().GetResult();
            Log.Info("[smoke-C] starting (BLOCKING prefix)...");
            var session = Voter.Start("smoke-C", new[] { "A", "B", "C" }, TimeSpan.FromSeconds(3));
            // ChatMessage positional ctor: (UserId, Login, DisplayName, Text,
            //                              ReceivedAt, IsSubscriber, IsModerator, IsVip)   // CHANGED: ctor comment — Opus
            chat.Inject(new ChatMessage(
                UserId: "carol-id", Login: "carol", DisplayName: "carol", Text: "#0",
                ReceivedAt: DateTimeOffset.UtcNow,
                IsSubscriber: false, IsModerator: false, IsVip: false));
            // The scary line: synchronously block the Godot main thread waiting for the
            // vote to complete. If Plan A's RunContinuationsAsynchronously design is
            // correct, this returns winner=0 in ~3s. If the dispatcher deadlocks, the
            // game hangs at this call — caught by the watchdog timer in Init().
            var winner = session.AwaitWinnerAsync().GetAwaiter().GetResult();
            Log.Info($"[smoke-C] BLOCKING winner={winner} (expected 0)");
            return winner;
        } catch (Exception e) {
            Log.Error($"[smoke-C] FAILED: {e}");
            return -1;
        }
    }

    private static async Task Run(FakeChatService chat, string label, string login,
                                  string userId, bool waitForPrior) {
        try {
            if (waitForPrior) {
                Log.Info($"[{label}] waiting for prior smoke to complete...");
                await ModEntry.SmokeATask;
            }
            Log.Info($"[{label}] starting (duration=3s, options=A,B,C)...");
            var session = Voter.Start(label, new[] { "A", "B", "C" }, TimeSpan.FromSeconds(3));
            chat.Inject(new ChatMessage(
                UserId: userId, Login: login, DisplayName: login, Text: "#0",
                ReceivedAt: DateTimeOffset.UtcNow,
                IsSubscriber: false, IsModerator: false, IsVip: false));

            // Watchdog: if AwaitWinnerAsync hangs, log a timeout instead of silent freeze.  // CHANGED: watchdog timeout — DeepSeek
            var winnerTask = session.AwaitWinnerAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
            var completed = await Task.WhenAny(winnerTask, timeoutTask);
            if (completed != winnerTask) {
                Log.Error($"[{label}] TIMEOUT: AwaitWinnerAsync did not complete within 15s");
                return;
            }

            var winner = await winnerTask;
            Log.Info($"[{label}] winner={winner} (expected 0) " +
                $"(continuation thread={Environment.CurrentManagedThreadId}, " +
                $"main thread={ModEntry.GodotMainThreadId})");                                // CHANGED: thread-ID — GPT5.5
        } catch (Exception e) {
            Log.Error($"[{label}] FAILED: {e}");
        }
    }
}
```

### `SmokeMainMenuPatch` (disposable, fire-and-forget — Smoke B)

```csharp
// SMOKE-TEST: DELETE AFTER VALIDATION.                                                    // CHANGED: header — 4 reviewers
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
internal static class SmokeMainMenuPatch {
    private static int _fired = 0;

    /// <summary>Validates Harmony picked up the patch attribute and resolved the target.</summary>  // CHANGED: Prepare validator — Opus
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
        Log.Info("[smoke-B] NMainMenu._Ready prefix fired (fire-and-forget)");              // CHANGED: prefix log — 4 reviewers
        _ = SmokeRunner.RunSmokeB(ModEntry.SmokeChat);
    }
}
```

### `SmokeBlockingPatch` (disposable, blocking — Smoke C)

```csharp
// SMOKE-TEST: DELETE AFTER VALIDATION. This is the scary one — if it deadlocks,
// the game hangs at the Settings menu. Force-quit and check logs for [smoke-C] entries.   // CHANGED: NEW patch — universal consensus
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
```

**Note:** The exact target for Smoke C (`NSettingsScreen._Ready` is a placeholder pending decompile verification) should be a benign main-menu-accessible screen distinct from `NMainMenu` so Smokes B and C don't collide on the same `_Ready`. The test plan: launch game → main menu fires Smoke B → click Settings → Settings menu fires Smoke C. If Settings menu hangs, deadlock confirmed.

## Flow

### Smoke A (ModInitializer context, fire-and-forget)

```
game loads slay_the_streamer_2.dll → ModEntry.Init() on Godot main thread
  Init: cast SceneTree, attach autoload, register singleton, wire dispatcher
  Init: TiLog.Sink → Log passthrough; Voter.Default = coord; Harmony.PatchAll
  Init: log patched method count; fire-and-forget SmokeRunner.RunSmokeA(SmokeChat)
RunSmokeA: Voter.Start(...), Inject "#0", await AwaitWinnerAsync() (with watchdog)
  ↓ (yields; Init returns; game continues startup)
3s elapse → close timer fires (threadpool)
  → dispatcher.Post(CloseNowInternal) → CallDeferred → next frame → CloseNowInternal
  → TCS.TrySetResult(0); Closed event; RunContinuationsAsynchronously schedules continuation
RunSmokeA continuation: Log.Info("[smoke-A] winner=0 (expected 0) (cont thread=N, main thread=M)")
```

### Smoke B (Harmony prefix, fire-and-forget)

```
player navigates to main menu → NMainMenu._Ready called on Godot main thread
SmokeMainMenuPatch.Prefix runs (synchronous void)
  log "[smoke-B] prefix fired"; first-time guard passes
  fire-and-forget RunSmokeB(SmokeChat)
RunSmokeB: await ModEntry.SmokeATask first (avoid race), then same shape as Smoke A
  ↓ (yields; Prefix returns; original NMainMenu._Ready continues)
~3-6s elapse → vote completes via same dispatcher path as Smoke A
RunSmokeB continuation: Log.Info("[smoke-B] winner=0 (expected 0)")
```

### Smoke C (Harmony prefix, BLOCKING — the scary one)

```
player opens Settings menu → NSettingsScreen._Ready called on Godot main thread
SmokeBlockingPatch.Prefix runs (synchronous void)
  log "[smoke-C] prefix fired"; first-time guard passes
  call RunSmokeCBlocking — synchronous, blocks the main thread
RunSmokeCBlocking: ModEntry.SmokeATask.GetAwaiter().GetResult() (wait for Smoke A)
  Voter.Start(...), Inject "#0"
  session.AwaitWinnerAsync().GetAwaiter().GetResult()  ← THE DEADLOCK CANDIDATE
    ↓ main thread is now blocked on AwaitWinnerAsync
  3s elapse → close timer fires (threadpool)
    → dispatcher.Post(CloseNowInternal) → CallDeferred queues for next frame
    → BUT main thread is blocked, so frame never advances...
  IF deadlocks: game hangs, watchdog cannot fire (also on blocked thread). Force-quit.
  IF works (RunContinuationsAsynchronously saves us): TCS resolves on threadpool,
    Result returns, prefix unblocks, original _Ready continues.
  Log.Info("[smoke-C] BLOCKING winner=0 (expected 0)")
```

## Success matrix

<!-- CHANGED: added Smoke C row(s) — universal consensus -->

| Smoke A | Smoke B | Smoke C | Conclusion |
|---|---|---|---|
| ✅ winner=0 | ✅ winner=0 | ✅ winner=0 | **Green light.** Plan B's blocking-await Harmony pattern is safe. `RunContinuationsAsynchronously` works as designed. |
| ✅ | ✅ | ❌ hang | **Deadlock confirmed.** The blocking-prefix pattern Plan B needs is unsafe with this dispatcher. Architecture pivot required (suspend-and-resume, transpiler, or different dispatcher). |
| ✅ | ✅ | ❌ wrong winner / TIMEOUT log | **Race or fault under blocking wait.** Investigate before Plan B; may be a synchronization issue specific to the blocking path. |
| ✅ | ❌ silent | n/a | Harmony prefix didn't apply (check `Prepare` log) or Smoke A's race blocked Smoke B's start (check serialization). |
| ✅ | ❌ hang | n/a | Smoke A's task never completed (Smoke B waits for it) — investigate the Smoke A flow first. |
| ❌ silent | n/a | n/a | Mod didn't load (check canary log) or autoload registration aborted (check error log). |
| ❌ hang at startup | n/a | n/a | Dispatcher's `CallDeferred` not receiving frame ticks; investigate `Engine.GetMainLoop() as SceneTree` cast and `Root.AddChild` outcome. |

## Build pipeline

`build.ps1` extension order: <!-- CHANGED: dotnet publish, parameterize game path — Opus, GPT5.5 -->

```powershell
param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

if (-not (Test-Path $GameInstall)) {
    throw "Game install not found: $GameInstall"
}

# 1. Refresh sts2.dll and 0Harmony.dll from game install.
$dataDir = Join-Path $GameInstall "data_sts2_windows_x86_64"
Copy-Item -Force (Join-Path $dataDir "sts2.dll")     "src/sts2.dll"
Copy-Item -Force (Join-Path $dataDir "0Harmony.dll") "src/0Harmony.dll"
$harmonyVer = (Get-Item "src/0Harmony.dll").VersionInfo.FileVersion
Write-Host "Using Harmony: $harmonyVer (from game install)"

# 2. Build via dotnet publish to a stable output path (avoid .godot/mono/temp drift).
dotnet publish src\slay_the_streamer_2.csproj -c Release -o dist\publish-tmp

# 3. Run Plan A regression tests.
dotnet test tests\slay_the_streamer_2.tests.csproj

# 4. Assemble dist/slay_the_streamer_2/.
$out = "dist/slay_the_streamer_2"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -Force "dist/publish-tmp/slay_the_streamer_2.dll" "$out/slay_the_streamer_2.dll"
Copy-Item -Force "src/slay_the_streamer_2.json"             "$out/slay_the_streamer_2.json"
Write-Host "Built dist/slay_the_streamer_2/"
```

`install.ps1`:

```powershell
param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
$src = "dist/slay_the_streamer_2"
$dst = Join-Path $GameInstall "mods/slay_the_streamer_2"
Write-Host "Installing from: $src"
Write-Host "Installing to:   $dst"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Force -Recurse "$src/*" $dst
Write-Host "Done. Launch the game and look for [slay_the_streamer_2] mod loading... in the log."
```

`uninstall.ps1`: <!-- CHANGED: added — Opus -->

```powershell
param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
$dst = Join-Path $GameInstall "mods/slay_the_streamer_2"
if (Test-Path $dst) {
    Remove-Item -Recurse -Force $dst
    Write-Host "Removed: $dst"
} else {
    Write-Host "Not installed at: $dst"
}
```

## csproj changes

Add to `src/slay_the_streamer_2.csproj`:

```xml
<Reference Include="0Harmony">
    <HintPath>0Harmony.dll</HintPath>
    <Private>false</Private>
</Reference>
```

`<Private>false</Private>` ensures `0Harmony.dll` is not copied into the output (the game already loads it).

## Outcome

**All three smokes succeed:**
1. Add a "Plan B prep smoke results: all green; blocking-await pattern validated" entry to `notes/06`.
2. Delete `src/Smoke/` directory entirely.
3. Remove the smoke-launcher block (step 6 + `SmokeATask` lines) from `ModEntry.Init()`.
4. Remove the static `SmokeChat`, `SmokeATask`, and (optionally) `GodotMainThreadId` fields. <!-- thread-ID logging is useful diagnostic, can stay -->
5. Commit cleanup as `plan-b/0.X: smoke test passed; remove disposable scaffolding`.
6. Proceed to Plan B writing.

**Smoke A green, Smoke B silent or hangs:**
1. Capture Harmony patch log output: did the patch apply?
2. If patch applied but prefix never fires, investigate `_Ready` lifecycle (timing, base-class declaration).
3. File follow-up; do not start Plan B.

**Smoke C hangs (deadlock):**
1. This is the architectural failure mode the smoke is designed to detect.
2. Plan A's `RunContinuationsAsynchronously` is insufficient given the Godot dispatcher.
3. Plan B redesign options: (a) suspend-and-resume Harmony pattern, (b) transpiler-based mutation, (c) different dispatcher (ConcurrentQueue + `_Process` drain).
4. Document the deadlock evidence in `notes/06`; do not start Plan B until resolved.

**Smoke A green, Smoke C wrong-winner / TIMEOUT:**
1. The blocking path completes but with wrong data — investigate FakeChatService injection timing under blocking wait, or potential race in the dispatcher.

## Risks and assumptions

- **`Engine.GetMainLoop()` may return null or a non-SceneTree** during `[ModInitializer]`. Mitigation: explicit cast + null check, abort with clear error log.
- **`MethodName.Run` source-generation** (replaced with string literal `"Run"` for portability).
- **`NMainMenu._Ready` and `NSettingsScreen._Ready` are stable patch targets** — `Prepare()` validators log the resolved targets so the streamer can verify before running the smoke.
- **`Harmony.PatchAll` correctly picks up `[HarmonyPatch]` attributes on internal types** — patch-count log surfaces this.
- **`MegaCrit.Sts2.Core.Logging.Log` is thread-safe** (validated in `notes/03`).
- **`FakeChatService.ConnectAsync` completes synchronously** — verified via Plan A test patterns; smoke-only `.GetResult()` is safe today, marked clearly as not-for-production.

## Future work (deferred from this spec)

These were proposed by reviewers but deemed out of scope for the smoke. If the smoke surfaces issues, these become Plan B alternatives:

- **`SynchronizationContext`-based dispatcher** — replace `DispatcherAutoload` Node entirely with a `SyncContext` captured at `Init()`. Lighter weight, but Godot may install its own context.
- **`SceneTree.CreateTimer` + signal** — alternative to `CallDeferred` if the latter has issues. More code per `Post` call.
- **`ConcurrentQueue<Action>` + `_Process` drain in `DispatcherAutoload`** — robust against `CallDeferred` quirks. Swap-in if `CallDeferred` based path fails.
- **Patch `_EnterTree` instead of `_Ready`** — earlier lifecycle hook if `_Ready` timing is unreliable.
- **Project-config autoload via a proxy class** — pre-register a thin proxy in `project.godot` that loads the real dispatcher at runtime. Sidesteps the runtime-attachment question entirely.

## What this smoke does NOT prove

- **It does not prove a Harmony prefix can substitute player input.** Smoke C blocks on a vote and logs the winner; it does not redirect the original game method's behavior. That validation is Plan B's actual hook work.
- **It does not test under load** — single vote per smoke, deterministic injection, no concurrent chat traffic.
- **It does not test reconnect or chat-disconnect handling** — `FakeChatService` stays connected throughout.
- **It does not validate the IRC parser or outgoing message queue** — those are unit-tested in Plan A.

---

## Optional Enhancements (pick what you want)

The following items were raised by reviewers but were not auto-applied. Specify by number which to incorporate:

1. **Project-autoload proxy class.** Pre-register a `DispatcherAutoloadProxy` Node in `project.godot` at build time; the proxy's `_Ready` instantiates the real `DispatcherAutoload`. Sidesteps the runtime-registration question entirely by using Godot's editor-blessed autoload path. — *Reviewer: GPT5.5* — *Effort: medium* — *My recommendation: lean no.* This adds a build-time dependency and a file just to avoid testing what we're explicitly trying to test. The smoke's job IS to validate runtime registration; switching to project-autoload defeats the purpose.

2. **`_Process` canary in `DispatcherAutoload`.** Override `_Process(double delta)` and log "DispatcherAutoload _Process tick" once on first invocation. Validates the node is actually receiving frame ticks separately from validating `CallDeferred`. — *Reviewer: GPT5.5* — *Effort: trivial* — *My recommendation: lean no.* `CallDeferred` only fires on a frame tick, so if the smoke completes, frame ticks are happening. The canary is redundant evidence.

3. **Verbose dispatcher logging.** Log every `Post` call's caller (via `[CallerMemberName]`) and the action's `Method.Name`. Useful for debugging dispatcher-related races. — *Reviewer: implicit (multiple)* — *Effort: trivial* — *My recommendation: lean no.* Noise during normal operation; turn on only if a smoke fails diagnostically.

4. **Switch `Interlocked.Exchange` to plain `if (_fired) return; _fired = true;`** in the smoke patches. Single-thread context, `Interlocked` is overkill. — *Reviewer: OwlAlpha* — *Effort: trivial* — *My recommendation: neutral.* Cosmetic. `Interlocked` is harmless and documents intent; plain bool is simpler.

5. **Replace `[HarmonyPatch(typeof(NSettingsScreen), "_Ready")]` with a method other reviewers recommend.** If `NSettingsScreen` doesn't exist in the decompile or has unexpected lifecycle, swap target. — *Reviewer: implicit* — *Effort: small (decompile lookup)* — *My recommendation: lean yes.* The placeholder name should be verified against the actual stable decompile before writing the smoke. I should grep `decompiled/sts2/` for a settings-related screen during plan creation.

6. **Add `[Collection("...")]` xUnit-style isolation note** in case the smoke ever spawns a unit-test variant. — *Reviewer: implicit* — *Effort: trivial* — *My recommendation: lean no.* The smoke is in-game; xUnit collections don't apply.

7. **Pin `0Harmony.dll` version in build output.** Currently `build.ps1` logs the version but doesn't pin. Add a check that fails build if Harmony version differs from expected. — *Reviewer: Opus* — *Effort: trivial* — *My recommendation: lean no.* The game ships its own Harmony; pinning would require updating our pin every game patch. Logging is sufficient.

8. **Run Smoke C against a test target that's safer than `_Ready`** — e.g., a button-click handler the streamer triggers explicitly. Gives more deterministic timing and makes the deadlock-vs-not signal cleaner. — *Reviewer: Opus, GPT5.5* — *Effort: small* — *My recommendation: lean yes if a suitable target exists.* `NSettingsScreen._Ready` is fine but a button-click that the streamer can trigger on demand is more diagnostic. Worth considering during plan-writing.

9. **Add `Voter.Default` null-check at end of `Init()`** as a sanity check before declaring success. — *Reviewer: Gemma4* — *Effort: trivial* — *My recommendation: lean yes.* One line, catches "Init completed but state is wrong" silently.

10. **Log Godot version and main loop type at startup.** `Engine.GetVersionInfo()` exposes this. — *Reviewer: OwlAlpha* — *Effort: trivial* — *My recommendation: lean yes.* Useful for cross-version troubleshooting. One log line.

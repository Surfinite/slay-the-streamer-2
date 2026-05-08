# Plan B prep — smoke test implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a minimum-viable mod that runs three smoke vote scenarios (one from `[ModInitializer]`, one from a fire-and-forget Harmony prefix, one from a blocking Harmony prefix) to validate Godot autoload registration from a runtime-loaded mod assembly and the Harmony-prefix-await deadlock concern, before writing Plan B's full implementation.

**Architecture:** A `[ModInitializer]` static class wires up a Godot `Node`-based main-thread dispatcher (`DispatcherAutoload` + `GodotMainThreadDispatcher : IMainThreadDispatcher`), then sets `Voter.Default` to a `VoteCoordinator` backed by `FakeChatService`. `Harmony.PatchAll` applies two disposable patches: one fire-and-forget on `NMainMenu._Ready`, one blocking on `NSettingsScreen._Ready`. The smoke runner injects `"#0"` and awaits the winner, logging via `TiLog.Sink → MegaCrit.Sts2.Core.Logging.Log`. Disposable files live in `src/Smoke/`; everything else is permanent Plan-B-Phase-1 scaffolding.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono (`Godot.NET.Sdk/4.5.1`), HarmonyLib (`0Harmony.dll` from game install), Plan A's TI core library (`src/Ti/`).

**Source spec:** `docs/superpowers/specs/2026-05-08-plan-b-prep-smoke-test-design-v2.md` (commit `63f2b3b`)

---

## Phase 0: Build pipeline scaffolding

### Task 0.1: Extend `build.ps1` to refresh `0Harmony.dll`, publish Release, and assemble `dist/`

**Files:**
- Modify: `build.ps1` (full rewrite — small file)

- [ ] **Step 1: Replace `build.ps1`**

```powershell
# Plan B prep build script.
# Refreshes sts2.dll + 0Harmony.dll from game install, builds Release config,
# runs Plan A regression tests, assembles dist/slay_the_streamer_2/.

param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $GameInstall)) {
    throw "Game install not found: $GameInstall"
}

$dataDir = Join-Path $GameInstall "data_sts2_windows_x86_64"
$gameSts2 = Join-Path $dataDir "sts2.dll"
$gameHarmony = Join-Path $dataDir "0Harmony.dll"

if (-not (Test-Path $gameSts2)) { throw "sts2.dll not found at $gameSts2." }
if (-not (Test-Path $gameHarmony)) { throw "0Harmony.dll not found at $gameHarmony." }

Copy-Item -Force $gameSts2 "src\sts2.dll"
Copy-Item -Force $gameHarmony "src\0Harmony.dll"
$harmonyVer = (Get-Item "src\0Harmony.dll").VersionInfo.FileVersion
Write-Host "Refreshed src\sts2.dll and src\0Harmony.dll (Harmony $harmonyVer)"

# Build via dotnet publish to a stable output path (avoid .godot/mono/temp drift).
dotnet publish src\slay_the_streamer_2.csproj -c Release -o dist\publish-tmp
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

dotnet test tests\slay_the_streamer_2.tests.csproj --nologo
if ($LASTEXITCODE -ne 0) { throw "Plan A regression tests failed" }

# Assemble dist/slay_the_streamer_2/
$out = "dist\slay_the_streamer_2"
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -Force "dist\publish-tmp\slay_the_streamer_2.dll" "$out\slay_the_streamer_2.dll"
Copy-Item -Force "src\slay_the_streamer_2.json" "$out\slay_the_streamer_2.json"
Write-Host "Built $out\"
Write-Host "Plan B prep build cycle: OK"
```

- [ ] **Step 2: Run the build**

Run: `pwsh -File build.ps1`
Expected: prints `Refreshed src\sts2.dll and src\0Harmony.dll (Harmony <version>)`, `Plan B prep build cycle: OK`. `dist/slay_the_streamer_2/` contains both files.

- [ ] **Step 3: Commit**

```powershell
git add build.ps1
git commit -m @'
plan-b-prep/0.1: build.ps1 refreshes 0Harmony.dll, publishes Release, assembles dist/

Adds 0Harmony.dll alongside sts2.dll refresh from the game install.
Switches from dotnet build to dotnet publish with explicit -o for a
stable output path (avoids .godot/mono/temp/bin/Release path drift).
Assembles dist/slay_the_streamer_2/ with the .dll + manifest, ready
for install.ps1 to copy into the game's mods folder.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 0.2: Create `install.ps1`

**Files:**
- Create: `install.ps1`

- [ ] **Step 1: Create `install.ps1`**

```powershell
param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

$src = "dist\slay_the_streamer_2"
$dst = Join-Path $GameInstall "mods\slay_the_streamer_2"

if (-not (Test-Path $src)) {
    throw "Source not found: $src. Run build.ps1 first."
}

Write-Host "Installing from: $src"
Write-Host "Installing to:   $dst"

New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Force -Recurse "$src\*" $dst

Write-Host ""
Write-Host "Done. Launch the game and look for the canary log line:"
Write-Host "  [slay_the_streamer_2] mod loading..."
Write-Host "in the Godot log at:"
Write-Host "  %APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\"
```

- [ ] **Step 2: Verify the script's parameter shape**

Run: `pwsh -Command "Get-Help .\install.ps1 -Detailed" 2>&1 | Select-String GameInstall`
Expected: shows the `GameInstall` parameter.

- [ ] **Step 3: Commit**

```powershell
git add install.ps1
git commit -m @'
plan-b-prep/0.2: install.ps1 copies dist/ to game mods folder

Parameterised game install path; echoes source/destination before
copy; prints log-canary hint after. Validates source exists before
attempting install (catches "forgot to build" error early).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 0.3: Create `uninstall.ps1`

**Files:**
- Create: `uninstall.ps1`

- [ ] **Step 1: Create `uninstall.ps1`**

```powershell
param(
    [string]$GameInstall = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)

$ErrorActionPreference = "Stop"

$dst = Join-Path $GameInstall "mods\slay_the_streamer_2"

if (Test-Path $dst) {
    Remove-Item -Recurse -Force $dst
    Write-Host "Removed: $dst"
} else {
    Write-Host "Not installed at: $dst"
}
```

- [ ] **Step 2: Commit**

```powershell
git add uninstall.ps1
git commit -m @'
plan-b-prep/0.3: uninstall.ps1 removes the mod from game's mods folder

Symmetry counterpart to install.ps1.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 1: csproj + dispatcher classes

### Task 1.1: Add `0Harmony.dll` reference to `src/slay_the_streamer_2.csproj`

**Files:**
- Modify: `src/slay_the_streamer_2.csproj`

- [ ] **Step 1: Refresh `0Harmony.dll` so the reference resolves at build time**

Run: `pwsh -File build.ps1`
Expected: `src/0Harmony.dll` exists after this runs.

- [ ] **Step 2: Modify `src/slay_the_streamer_2.csproj` — add Harmony reference**

Replace the existing `<ItemGroup>` block:

```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>SlayTheStreamer2</RootNamespace>
    <AssemblyName>slay_the_streamer_2</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="sts2">
      <HintPath>sts2.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Verify the build still passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. The 142 Plan A tests still pass; the .csproj now resolves `HarmonyLib` types (verified in Task 1.2 when we use them).

- [ ] **Step 4: Commit**

```powershell
git add src/slay_the_streamer_2.csproj
git commit -m @'
plan-b-prep/1.1: csproj references 0Harmony.dll (Private=false)

Reference resolves at build via the local 0Harmony.dll refreshed by
build.ps1 from the game install. Private=false avoids copying the
DLL into our output (game already loads it; double-load would cause
type-load conflicts).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 1.2: Create `DispatcherAutoload` (Godot Node)

**Files:**
- Create: `src/Godot/DispatcherAutoload.cs`

- [ ] **Step 1: Create `src/Godot/DispatcherAutoload.cs`**

```csharp
using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace SlayTheStreamer2.Godot;

/// <summary>
/// Runtime-attached Node that hops arbitrary Actions onto the Godot main thread
/// via CallDeferred. Despite the name, this is NOT a Godot project autoload
/// configured in ProjectSettings; the class name preserves the connection to
/// notes/06 item #6 (validate autoload registration from a mod assembly).
/// </summary>
public partial class DispatcherAutoload : Node {
    public void Post(Action action) {
        ArgumentNullException.ThrowIfNull(action);
        // String literal "Run" instead of MethodName.Run for portability across
        // Godot source-generator versions.
        // Explicit (Action) cast avoids overload-resolution ambiguity in Callable.From.
        CallDeferred("Run", Callable.From((Action)action));
    }

    private void Run(Callable callable) {
        try {
            callable.Call();
        } catch (Exception e) {
            Log.Error($"[slay_the_streamer_2] DispatcherAutoload.Run threw: {e}");
        }
    }
}
```

- [ ] **Step 2: Verify build still passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. (No unit tests for this file — `CallDeferred` requires a live Godot main loop. The smoke is the test.)

- [ ] **Step 3: Commit**

```powershell
git add src/Godot/DispatcherAutoload.cs
git commit -m @'
plan-b-prep/1.2: DispatcherAutoload — Godot Node, Post(Action) via CallDeferred

Single-responsibility node: queue an Action onto the next Godot main-thread
frame. String literal "Run" instead of MethodName.Run for source-gen
portability; explicit Action cast on Callable.From for overload-resolution
safety; try/catch wraps the deferred call so dispatcher action exceptions
are logged rather than bubbling into Godot's loop.

The class name (DispatcherAutoload) preserves the connection to
notes/06 #6; it is NOT a Godot project autoload.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 1.3: Create `GodotMainThreadDispatcher : IMainThreadDispatcher`

**Files:**
- Create: `src/Godot/GodotMainThreadDispatcher.cs`

- [ ] **Step 1: Create `src/Godot/GodotMainThreadDispatcher.cs`**

```csharp
using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Godot;

/// <summary>
/// IMainThreadDispatcher implementation backed by a registered DispatcherAutoload
/// Node. Post forwards to the autoload's CallDeferred-based queue.
/// </summary>
public sealed class GodotMainThreadDispatcher : IMainThreadDispatcher {
    private DispatcherAutoload? _autoload;

    public void SetAutoload(DispatcherAutoload a) => _autoload = a;

    public void Post(Action action) =>
        (_autoload ?? throw new InvalidOperationException(
            "GodotMainThreadDispatcher.Post called before SetAutoload."))
            .Post(action);

    /// <summary>
    /// Barrier-Post: completes after previously-posted actions have run.
    /// Does NOT recursively drain actions posted during the drain. Plan A
    /// production code does not call DrainAsync (verified by grep of src/);
    /// only test code uses it.
    /// </summary>
    public Task DrainAsync() {
        var tcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
```

- [ ] **Step 2: Verify build still passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. The class fully implements `IMainThreadDispatcher`'s `Post(Action)` and `Task DrainAsync()` methods; compile succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src/Godot/GodotMainThreadDispatcher.cs
git commit -m @'
plan-b-prep/1.3: GodotMainThreadDispatcher — IMainThreadDispatcher impl

Post forwards to DispatcherAutoload.Post (CallDeferred-based hop onto
Godot main thread). DrainAsync uses barrier-Post pattern: completes
after previously-posted actions have run, does not recursively drain.
Plan A production code does not call DrainAsync; only test code does.

SetAutoload is the wire-up point called from ModEntry.Init() after the
node is attached to the SceneTree.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 2: ModEntry permanent shape

### Task 2.1: Replace `ModEntry.cs` with `[ModInitializer]` permanent wiring (no smoke yet)

**Files:**
- Replace: `src/ModEntry.cs`

- [ ] **Step 1: Replace `src/ModEntry.cs`**

```csharp
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
            GodotMainThreadId = Environment.CurrentManagedThreadId;
            Log.Info($"[slay_the_streamer_2] mod loading... (init thread={GodotMainThreadId})");

            // Godot version + main loop type for cross-version troubleshooting.
            var godotVer = Engine.GetVersionInfo();
            Log.Info($"[slay_the_streamer_2] Godot {godotVer["string"]}, " +
                $"main loop type: {Engine.GetMainLoop()?.GetType().Name ?? "<null>"}");
            Log.Info($"[slay_the_streamer_2] log file location: " +
                $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}" +
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
                    case LogLevel.Error: Log.Error(ex is null ? msg : $"{msg} :: {ex}"); break;
                    case LogLevel.Warn:  Log.Warn(msg); break;
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
```

- [ ] **Step 2: Verify build passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`.

- [ ] **Step 3: Commit**

```powershell
git add src/ModEntry.cs
git commit -m @'
plan-b-prep/2.1: ModEntry [ModInitializer] permanent wiring

Resolves SceneTree via explicit cast + null check (Engine.GetMainLoop()
returns MainLoop, not SceneTree — caught by GPT5.5 in meta-review).
Attaches DispatcherAutoload Node as SceneTree.Root child (primary).
Tries Engine.RegisterSingleton as optional instrumentation (logs warn
on failure, continues). Wires GodotMainThreadDispatcher to the
autoload. Sets TiLog.Sink for game-log passthrough (Logger thread-
safety verified in notes/03). Calls Harmony.PatchAll with diagnostic
logging (patched method count + names; warns on zero). Top-level
try/catch bounds the blast radius if Init throws.

Smoke wiring (FakeChatService + Voter.Default + smoke launchers) lands
in Task 4.1 as the disposable layer over this skeleton.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 3: Smoke runner

### Task 3.1: Create `Smoke/SmokeRunner.cs`

**Files:**
- Create: `src/Smoke/SmokeRunner.cs`

- [ ] **Step 1: Create `src/Smoke/SmokeRunner.cs`**

```csharp
// SMOKE-TEST: DELETE AFTER VALIDATION.
using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Smoke;

public static class SmokeRunner {
    public static async Task RunSmokeA(FakeChatService chat) =>
        await Run(chat, "smoke-A", "alice", "alice-id", waitForPrior: false);

    public static async Task RunSmokeB(FakeChatService chat) =>
        await Run(chat, "smoke-B", "bob", "bob-id", waitForPrior: true);

    /// <summary>Smoke C: blocking-await variant. Validates Plan B's realistic pattern.</summary>
    public static int RunSmokeCBlocking(FakeChatService chat) {
        try {
            // Wait for prior smokes to complete (avoid VoteCoordinator single-session race).
            ModEntry.SmokeATask.GetAwaiter().GetResult();
            Log.Info("[smoke-C] starting (BLOCKING prefix)...");
            var session = Voter.Start("smoke-C", new[] { "A", "B", "C" }, TimeSpan.FromSeconds(3));
            // ChatMessage positional ctor:
            //   (UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip)
            chat.Inject(new ChatMessage(
                UserId: "carol-id", Login: "carol", DisplayName: "carol", Text: "#0",
                ReceivedAt: DateTimeOffset.UtcNow,
                IsSubscriber: false, IsModerator: false, IsVip: false));
            // The scary line: synchronously block the Godot main thread waiting for the
            // vote to complete. If Plan A's RunContinuationsAsynchronously design is
            // correct, this returns winner=0 in ~3s. If the dispatcher deadlocks, the
            // game hangs at this call.
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

            // Watchdog: if AwaitWinnerAsync hangs, log a timeout instead of silent freeze.
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
                $"main thread={ModEntry.GodotMainThreadId})");
        } catch (Exception e) {
            Log.Error($"[{label}] FAILED: {e}");
        }
    }
}
```

- [ ] **Step 2: Verify build fails as expected**

Run: `pwsh -File build.ps1`
Expected: build fails with errors about `ModEntry.SmokeATask` not existing. This is intentional — Task 4.1 adds it.

- [ ] **Step 3: Don't commit yet — Task 4.1 introduces the missing field**

(SmokeRunner.cs is on disk but not yet compilable; Task 4.1 makes it compile.)

---

## Phase 4: ModEntry smoke wiring

### Task 4.1: Add disposable smoke wiring to `ModEntry.Init()`

**Files:**
- Modify: `src/ModEntry.cs`

- [ ] **Step 1: Modify `src/ModEntry.cs` — add smoke fields and wire smoke launcher**

Add `using` directives at the top (if not already present):

```csharp
using System.Threading.Tasks;
using SlayTheStreamer2.Smoke;
using SlayTheStreamer2.Ti.Chat;
```

Add the smoke fields right inside the `ModEntry` class declaration, before `Init()`:

```csharp
    // SMOKE-TEST: DELETE AFTER VALIDATION.
    internal static FakeChatService SmokeChat = null!;
    internal static Task SmokeATask = Task.CompletedTask;
```

Replace the comment block at section 6 (`// 6. (Smoke wiring will be added...`) with the actual wiring:

```csharp
            // 6. Smoke wiring (DISPOSABLE — delete after smoke success).
            // SMOKE-ONLY: FakeChatService.ConnectAsync completes synchronously.
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
```

Replace the comment block at section 8 (`// 8. Sanity check: Voter.Default should be set...`) with:

```csharp
            // 8. Sanity check before declaring success.
            if (Voter.Default is null) {
                Log.Error("[slay_the_streamer_2] FATAL: Voter.Default is null after wiring; " +
                    "smoke cannot run. Aborting.");
                return;
            }

            // 9. Smoke A: fire-and-forget vote from this startup context (DISPOSABLE).
            SmokeATask = SmokeRunner.RunSmokeA(SmokeChat);
```

- [ ] **Step 2: Verify build passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. SmokeRunner.cs now compiles (the missing `ModEntry.SmokeATask` and `ModEntry.SmokeChat` references resolve).

- [ ] **Step 3: Commit**

```powershell
git add src/ModEntry.cs src/Smoke/SmokeRunner.cs
git commit -m @'
plan-b-prep/4.1: ModEntry smoke wiring + SmokeRunner

Adds disposable smoke layer to ModEntry.Init: FakeChatService, Plan A
SystemClock + SystemTimerScheduler, VoteCoordinator wired with the
GodotMainThreadDispatcher, Voter.Default assignment, Voter.Default
null-check, and fire-and-forget RunSmokeA call. SmokeATask is exposed
internally so Smokes B and C can wait on it (avoids VoteCoordinator
single-session race per the meta-review).

SmokeRunner has RunSmokeA / RunSmokeB / RunSmokeCBlocking with
ChatMessage.Inject deterministic injection (#0 → expected winner=0),
15s watchdog timeout, and thread-ID logging on completion to surface
"is the continuation actually on a different thread than init?" as
observable evidence rather than implicit assumption.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 5: Smoke B (Harmony fire-and-forget)

### Task 5.1: Create `Smoke/SmokeMainMenuPatch.cs`

**Files:**
- Create: `src/Smoke/SmokeMainMenuPatch.cs`

- [ ] **Step 1: Create `src/Smoke/SmokeMainMenuPatch.cs`**

```csharp
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
```

- [ ] **Step 2: Verify build passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`.

- [ ] **Step 3: Commit**

```powershell
git add src/Smoke/SmokeMainMenuPatch.cs
git commit -m @'
plan-b-prep/5.1: Smoke B — Harmony prefix on NMainMenu._Ready (fire-and-forget)

Disposable patch. Prepare() validator logs the resolved target so
"silent Smoke B" can be diagnosed as "patch never applied" vs
"patch applied but prefix not fired yet." Interlocked-guarded
first-fire-only via atomic flag. Fire-and-forget RunSmokeB so the
prefix returns immediately and original _Ready continues.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 6: Smoke C (Harmony blocking)

### Task 6.1: Create `Smoke/SmokeBlockingPatch.cs`

**Files:**
- Create: `src/Smoke/SmokeBlockingPatch.cs`

- [ ] **Step 1: Create `src/Smoke/SmokeBlockingPatch.cs`**

```csharp
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
```

- [ ] **Step 2: Verify build passes**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. The published `dist/slay_the_streamer_2/slay_the_streamer_2.dll` is now a complete smoke-test mod.

- [ ] **Step 3: Commit**

```powershell
git add src/Smoke/SmokeBlockingPatch.cs
git commit -m @'
plan-b-prep/6.1: Smoke C — Harmony prefix on NSettingsScreen._Ready (BLOCKING)

The scary one: synchronously blocks the Godot main thread on
AwaitWinnerAsync().GetAwaiter().GetResult() — the realistic Plan B
pattern (Harmony prefix waits for chat vote winner before returning).
If Plan A's RunContinuationsAsynchronously design holds, this completes
in ~3s. If the dispatcher deadlocks (CallDeferred queue blocked by
the held main thread), the game hangs at Settings.

NSettingsScreen target chosen over Start-Run / Neow-load: a 3s pause
at Settings looks like UI lag, while a 3s pause at Start-Run looks
like a real bug. Settings has zero gameplay implications. The smoke
is validating the dispatch mechanism, not the realistic Plan-B
context — Neow / card reward / etc. are Plan B's actual targets.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 7: Validation (user-driven)

### Task 7.1: Build, install, run, and report

This task is operator-driven and not automated. The plan executor (you, the agent) cannot run StS2 in-game; the user must.

**Files:**
- Read: game log file at `%APPDATA%\Godot\app_userdata\Slay the Spire 2\logs\`

- [ ] **Step 1: Build the mod**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. `dist/slay_the_streamer_2/` contains the .dll and .json.

- [ ] **Step 2: Install the mod**

Run: `pwsh -File install.ps1`
Expected: prints `Installed.` with hint about the canary log line.

- [ ] **Step 3: User launches the game and verifies log output**

The user (Surfinite) launches StS2 from Steam. The smoke runs in this order:
- **At launch** (Smoke A): `[slay_the_streamer_2] mod loading...` should appear within ~1 second; `[smoke-A] starting...` shortly after; `[smoke-A] winner=0 (expected 0)` 3 seconds later.
- **At main menu** (Smoke B): `[smoke-B] Prepare: target resolved as ...` should have appeared during init; `[smoke-B] NMainMenu._Ready prefix fired (fire-and-forget)` when the menu appears; `[smoke-B] winner=0` 3 seconds after.
- **At settings menu** (Smoke C): user clicks Settings. `[smoke-C] NSettingsScreen._Ready prefix fired (BLOCKING)` should appear, then `[smoke-C] BLOCKING winner=0 (expected 0)` 3 seconds later. **If the Settings menu hangs, the deadlock concern is confirmed.**

- [ ] **Step 4: User reports the outcome**

The user shares the log file (or relevant excerpts) with the agent. The agent reads the log and walks through the success matrix in the v2 spec to determine the conclusion:

| Smoke A | Smoke B | Smoke C | Conclusion |
|---|---|---|---|
| ✅ winner=0 | ✅ winner=0 | ✅ winner=0 | **Green light.** Proceed to Phase 8 (cleanup) and start writing Plan B. |
| ✅ | ✅ | ❌ hang | **Deadlock confirmed.** Document evidence in `notes/06`; do NOT start Plan B. |
| ✅ | ✅ | ❌ wrong-winner / TIMEOUT log | Race or fault under blocking wait — investigate. |
| ✅ | ❌ silent | n/a | Harmony patch didn't apply (check `Prepare` log) or Smoke A's race blocked B's start. |
| ❌ silent | n/a | n/a | Mod didn't load (check canary). |
| ❌ hang at startup | n/a | n/a | Dispatcher / autoload not receiving frame ticks. |

- [ ] **Step 5: No commit — log analysis is interpretive**

This task does not produce a code commit. The outcome determines whether to proceed to Phase 8 (success path) or freeze and investigate (failure path). The agent updates the user's understanding by quoting the relevant log lines and stating the success-matrix row matched.

---

## Phase 8: Cleanup (conditional on success)

### Task 8.1: Delete disposable smoke files and clean up `ModEntry.Init()`

**Only execute this task if all three smokes succeeded in Task 7.1.**

**Files:**
- Delete: `src/Smoke/SmokeRunner.cs`
- Delete: `src/Smoke/SmokeMainMenuPatch.cs`
- Delete: `src/Smoke/SmokeBlockingPatch.cs`
- Delete: `src/Smoke/` directory (after files removed)
- Modify: `src/ModEntry.cs`
- Modify: `notes/06-followups-and-deferred.md`

- [ ] **Step 1: Delete the smoke files**

Run:
```powershell
Remove-Item -Recurse -Force src/Smoke
```

- [ ] **Step 2: Modify `src/ModEntry.cs` — remove disposable layers**

Remove the `using SlayTheStreamer2.Smoke;` and `using System.Threading.Tasks;` (if Tasks is no longer used elsewhere) directives. Remove the smoke fields:

```csharp
    // DELETE these lines:
    internal static FakeChatService SmokeChat = null!;
    internal static Task SmokeATask = Task.CompletedTask;
```

Remove the entire smoke-wiring block (section 6, the FakeChatService + Coordinator construction) — Plan B will replace this with `TwitchIrcChatService` wiring.

Remove the `SmokeATask = SmokeRunner.RunSmokeA(SmokeChat);` line in section 9.

The `Voter.Default` null-check in section 8 stays (good defensive habit for Plan B).

The `GodotMainThreadThreadId` field stays (useful diagnostic for Plan B).

- [ ] **Step 3: Modify `notes/06-followups-and-deferred.md`**

Mark items #6 and #7 in the "Pre-Plan-B prep" section as resolved:

Find the lines:
```markdown
- [ ] **Validate Godot autoload registration from a mod assembly.** ...
- [ ] **Smoke-test the Harmony deadlock risk** ...
```

Replace with:
```markdown
- [x] **Validate Godot autoload registration from a mod assembly.** Resolved by Plan B prep smoke test (2026-05-08). `Engine.GetMainLoop() as SceneTree → Root.AddChild(node)` works; node receives frame ticks and CallDeferred dispatches successfully. `Engine.RegisterSingleton` was attempted as optional instrumentation and also succeeded. See `docs/superpowers/specs/2026-05-08-plan-b-prep-smoke-test-design-v2.md`.
- [x] **Smoke-test the Harmony deadlock risk.** Resolved by Plan B prep smoke test (2026-05-08). Smoke C (blocking `AwaitWinnerAsync().GetAwaiter().GetResult()` from a Harmony prefix on `NSettingsScreen._Ready`) completed successfully. Plan A's `RunContinuationsAsynchronously` on `_winnerTcs` is sufficient to avoid main-thread deadlock under blocking wait. Plan B can use the blocking-prefix pattern with confidence.
```

- [ ] **Step 4: Verify build still passes after cleanup**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`. ModEntry now has the permanent shape only — no smoke imports, no smoke fields, no smoke launcher. `Voter.Default` is still set (by remaining permanent wiring? — wait, no, the FakeChatService construction was the smoke-only path. Plan B Phase 1 will set Voter.Default; for now, it is null after cleanup, which is intentional: the mod loads but does nothing without Plan B).

The Voter.Default null-check at the end of Init will now log "FATAL: Voter.Default is null" and return — that is the expected behavior post-cleanup-pre-Plan-B. The mod is in a "ready for Plan B Phase 1" state.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m @'
plan-b-prep/8.1: smoke test passed; remove disposable scaffolding

All three smokes green: A (ModInitializer fire-and-forget), B (Harmony
prefix fire-and-forget on NMainMenu._Ready), C (Harmony prefix BLOCKING
on NSettingsScreen._Ready). Plan A's RunContinuationsAsynchronously
design validated under realistic conditions; Godot autoload registration
via Engine.GetMainLoop() as SceneTree → Root.AddChild works.

Removes src/Smoke/ directory entirely. Cleans the disposable layer out
of ModEntry.Init: FakeChatService construction, smoke fields,
RunSmokeA launcher. Permanent scaffolding (DispatcherAutoload,
GodotMainThreadDispatcher, [ModInitializer] permanent shape with
SceneTree cast / autoload attach / Harmony.PatchAll diagnostic logging
/ TiLog.Sink passthrough / Voter.Default null-check / top-level
try/catch) is preserved as Plan B Phase 1 starting point.

notes/06: items #6 and #7 in Pre-Plan-B prep marked resolved with
references to spec/plan files.

Plan B writing can begin.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Self-review notes

**Spec coverage:**
- ✅ Smoke A (ModInitializer fire-and-forget): Tasks 2.1 + 4.1.
- ✅ Smoke B (Harmony fire-and-forget on NMainMenu._Ready): Task 5.1.
- ✅ Smoke C (Harmony BLOCKING on NSettingsScreen._Ready): Task 6.1.
- ✅ DispatcherAutoload: Task 1.2.
- ✅ GodotMainThreadDispatcher with full IMainThreadDispatcher (Post + DrainAsync barrier-Post): Task 1.3.
- ✅ ModEntry permanent shape (SceneTree cast, AddChild primary, RegisterSingleton optional, TiLog.Sink, Harmony.PatchAll + diagnostic, Voter.Default null-check, top-level try/catch, thread-ID, version log, log-path hint): Task 2.1.
- ✅ ModEntry disposable smoke wiring: Task 4.1.
- ✅ csproj 0Harmony reference: Task 1.1.
- ✅ build.ps1 / install.ps1 / uninstall.ps1: Phase 0.
- ✅ Validation flow: Task 7.1.
- ✅ Cleanup post-success: Task 8.1.
- ✅ All four user-picked optional enhancements (5, 8, 9, 10): folded into Tasks 2.1 and 6.1.

**Type consistency:** verified — `Voter.Start(label, options, duration)`, `VoteSession.AwaitWinnerAsync()`, `FakeChatService.Inject(ChatMessage)`, `IMainThreadDispatcher.Post(Action)` and `DrainAsync()`, `VoteCoordinator(IChatService, IClock, ITimerScheduler, IMainThreadDispatcher)` all match Plan A's actual signatures (verified earlier in the meta-review by direct file reads).

**No placeholders:** every step has the exact code or command.

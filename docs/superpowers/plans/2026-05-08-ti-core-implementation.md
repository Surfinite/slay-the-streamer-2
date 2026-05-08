# TI Core Library Implementation Plan (Plan A)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the testable TI core library — `ChatService` data types and `FakeChatService`, the `VoteSession` / `VoteCoordinator` / `Voter` voting engine, the `TwitchIrcParser` and `OutgoingMessageQueue` for the eventual `TwitchIrcChatService`, plus all the deterministic-testing scaffolding (`IClock`, `ITimerScheduler`, `IMainThreadDispatcher`, `ITiLogger`). At the end of Plan A, `dotnet test` passes a comprehensive xUnit suite; the mod doesn't yet load in StS2 (that's Plan B).

**Architecture:** Two-tier `IChatService` (data; no vote semantics) → `VoteSession` (engine; consumes `IChatService`) per the v2.1 spec. All effectful types (clock, timer, dispatcher, logger, RNG) are injected behind interfaces with `Fake*` test impls. `ImmediateDispatcher` lets every voting test run on the calling thread without any Godot or threading. Sources live in `src/Ti/{Internal,Chat,Voting}/` (no Godot in any of those folders); tests source-reference them from `tests/`.

**Tech Stack:** C# / .NET 9 ; `Microsoft.NET.Sdk` for the test csproj ; `Godot.NET.Sdk/4.5.1` for the main mod csproj (compilation target only — Plan A doesn't exercise Godot at runtime) ; xUnit for tests ; HarmonyLib (referenced from `0Harmony.dll` shipped with the game ; not actually exercised in Plan A).

**Spec:** [`../specs/2026-05-08-ti-layer-design-v2.md`](../specs/2026-05-08-ti-layer-design-v2.md). Every Plan A task implements one or more decisions from the v2.1 spec; cross-references in the form `(spec §<name>)` appear throughout.

---

## Scope: Plan A only

The v2.1 spec covers three reasonably-decoupled sub-systems. Each plan can produce working, testable software on its own:

- **Plan A (this plan)** — the dependency-free TI core library + tests. Produces a buildable `slay_the_streamer_2.dll` that compiles cleanly + a `dotnet test`-passing test project. Doesn't connect to Twitch IRC, doesn't render any UI, doesn't initialise as a mod.
- **Plan B (next)** — `TwitchIrcChatService` (the real IRC client built on top of Plan A's parser + queue), `GodotMainThreadDispatcher` + autoload, `VoteOverlayControl` and `ChatStatusControl` UI, and `ModEntry` wiring. Brings the mod to life in StS2.
- **Plan C (small standalone)** — IRC fixture generator console tool in `tools/irc-fixture-generator/` for capturing live Twitch IRC into the parser test corpus.

Plan A is the bulk of the surface area and the part most worth careful TDD. Plans B and C will be written after Plan A is implemented and reviewed.

## File structure produced by Plan A

```
slay-the-streamer-2/
├── src/
│   ├── slay_the_streamer_2.csproj                  Plan A: created
│   ├── slay_the_streamer_2.json                    Plan A: created (manifest)
│   ├── project.godot                               Plan A: created (minimal)
│   ├── icon.svg                                    Plan A: created (placeholder)
│   ├── ModEntry.cs                                 Plan A: empty stub; populated in Plan B
│   └── Ti/
│       ├── Internal/
│       │   ├── IClock.cs
│       │   ├── SystemClock.cs
│       │   ├── FakeClock.cs
│       │   ├── ITimerScheduler.cs
│       │   ├── SystemTimerScheduler.cs
│       │   ├── FakeTimerScheduler.cs
│       │   ├── IMainThreadDispatcher.cs
│       │   ├── ImmediateDispatcher.cs
│       │   └── TiLog.cs
│       ├── Chat/
│       │   ├── IChatService.cs
│       │   ├── ChatMessage.cs
│       │   ├── ChatCredentials.cs
│       │   ├── ChatConnectionState.cs
│       │   ├── ChatConnectionChangedEventArgs.cs
│       │   ├── OutgoingMessagePriority.cs
│       │   ├── FakeChatService.cs
│       │   └── Internal/
│       │       ├── TwitchIrcParser.cs
│       │       └── OutgoingMessageQueue.cs
│       └── Voting/
│           ├── VoteCoordinator.cs
│           ├── Voter.cs
│           ├── VoteSession.cs
│           ├── VoteOption.cs
│           ├── VoteSessionState.cs
│           ├── VoteReceiptPolicy.cs
│           ├── VoteParsingPolicy.cs
│           ├── ReceiptKind.cs
│           └── EnglishReceipts.cs
├── tests/
│   ├── slay_the_streamer_2.tests.csproj            Plan A: created
│   ├── Internal/
│   │   ├── FakeClockTests.cs
│   │   ├── FakeTimerSchedulerTests.cs
│   │   ├── ImmediateDispatcherTests.cs
│   │   └── TiLogTests.cs
│   ├── Chat/
│   │   ├── ChatCredentialsTests.cs
│   │   ├── FakeChatServiceTests.cs
│   │   └── Internal/
│   │       ├── TwitchIrcParserTests.cs
│   │       └── OutgoingMessageQueueTests.cs
│   └── Voting/
│       ├── VoteSessionTests.cs
│       ├── VoteCoordinatorTests.cs
│       └── EnglishReceiptsTests.cs
├── build.ps1                                       Plan A: created (skeleton; full Godot build is Plan B)
└── .gitignore                                      Plan A: extended
```

## Build & test commands

The plan uses these commands repeatedly. Memorise them:

| Command | Purpose |
|---|---|
| `dotnet build src/slay_the_streamer_2.csproj` | Compile the main mod assembly. Fails if a `Ti/*` type doesn't compile. |
| `dotnet test tests/slay_the_streamer_2.tests.csproj` | Run the full xUnit suite. |
| `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~<TestClass>"` | Run one test class. |
| `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~<TestClass>.<TestMethod>"` | Run one test method. |

Use the filtered command while iterating on a single test; use the full-suite command before each commit.

---

## Phase 0: Project scaffolding (3 tasks)

### Task 0.1: Create the main mod csproj

**Files:**
- Create: `src/slay_the_streamer_2.csproj`
- Create: `src/slay_the_streamer_2.json`
- Create: `src/project.godot`
- Create: `src/icon.svg`
- Create: `src/ModEntry.cs`

- [ ] **Step 1: Copy `sts2.dll` reference into `src/`**

The csproj needs a local `sts2.dll` for compilation. The build script will refresh this on each build, but for the first compile we need it manually.

Run:
```powershell
Copy-Item "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll" src\sts2.dll
```

Expected: file at `src/sts2.dll`. Verify with `Test-Path src\sts2.dll` returning `True`.

- [ ] **Step 2: Create `src/slay_the_streamer_2.csproj`**

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
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `src/slay_the_streamer_2.json`** (mod manifest)

```json
{
    "id": "slay_the_streamer_2",
    "name": "Slay the Streamer 2",
    "author": "Surfinite",
    "description": "Twitch chat votes on the streamer's choices. Inspired by Tempus's StS1 mod of the same name.",
    "version": "0.1.0",
    "has_pck": false,
    "has_dll": true,
    "dependencies": [],
    "affects_gameplay": true
}
```

- [ ] **Step 4: Create `src/project.godot`** (minimal — Godot needs *something* here for the SDK to be happy)

```
; Engine configuration file.
config_version=5

[application]

config/name="slay_the_streamer_2"
config/features=PackedStringArray("4.5", "C#", "Forward Plus")
config/icon="res://icon.svg"

[dotnet]

project/assembly_name="slay_the_streamer_2"
```

- [ ] **Step 5: Create `src/icon.svg`** (placeholder — Godot won't compile without it)

```svg
<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128"><rect width="128" height="128" fill="#222"/><text x="64" y="74" font-family="sans-serif" font-size="48" text-anchor="middle" fill="#eee">STS2</text></svg>
```

- [ ] **Step 6: Create `src/ModEntry.cs`** (Plan A: empty placeholder — wired up in Plan B)

```csharp
namespace SlayTheStreamer2;

// Plan A: placeholder. Plan B fills this in with [ModInitializer] + service wiring.
internal static class ModEntry {
}
```

- [ ] **Step 7: Verify the main project compiles**

Run: `dotnet build src/slay_the_streamer_2.csproj`
Expected: `Build succeeded.` with 0 errors. Warnings about Godot-SDK-specific things are fine.

- [ ] **Step 8: Extend `.gitignore`** to exclude the copied DLL and Godot build artefacts (most are already there)

Append to `.gitignore`:
```
# sts2.dll is copied per-build from the game install; never commit it
src/sts2.dll
```

- [ ] **Step 9: Commit**

```powershell
git add src/slay_the_streamer_2.csproj src/slay_the_streamer_2.json src/project.godot src/icon.svg src/ModEntry.cs .gitignore
git commit -m @'
plan-a/0.1: scaffold main mod csproj

Empty ModEntry placeholder; Godot.NET.Sdk csproj compiles cleanly
against a local copy of sts2.dll. Ti/* source files come in
subsequent tasks.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 0.2: Create the test csproj

**Files:**
- Create: `tests/slay_the_streamer_2.tests.csproj`
- Create: `tests/_Sanity.cs`

- [ ] **Step 1: Create `tests/slay_the_streamer_2.tests.csproj`**

The test project uses `Microsoft.NET.Sdk` (no Godot needed at runtime) and source-references the non-Godot `Ti/*` folders directly. UI files (`Ti/Ui/*`, `Ti/Godot/*`) are excluded — they don't exist yet (Plan B) and they'd pull in Godot.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
    <RootNamespace>SlayTheStreamer2.Tests</RootNamespace>
  </PropertyGroup>

  <!-- Source-reference the non-Godot Ti/* folders. Plan A only touches these. -->
  <ItemGroup>
    <Compile Include="..\src\Ti\Internal\**\*.cs" />
    <Compile Include="..\src\Ti\Chat\**\*.cs" />
    <Compile Include="..\src\Ti\Voting\**\*.cs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create a sanity test** so we can verify the test runner is wired up before any production source exists

`tests/_Sanity.cs`:
```csharp
using Xunit;

namespace SlayTheStreamer2.Tests;

public class _Sanity {
    [Fact]
    public void TestRunnerIsAlive() {
        Assert.Equal(2, 1 + 1);
    }
}
```

- [ ] **Step 3: Run the test suite to verify**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: `Passed: 1, Failed: 0`. (The `<Compile Include="..\src\Ti\...">` patterns don't error on missing folders — they just match nothing — so the test project compiles even before any `Ti/*` files exist.)

- [ ] **Step 4: Commit**

```powershell
git add tests/slay_the_streamer_2.tests.csproj tests/_Sanity.cs
git commit -m @'
plan-a/0.2: scaffold test csproj with xUnit and source-referenced Ti/*

Test project source-references src/Ti/{Internal,Chat,Voting}/**/*.cs;
no Godot dependency. _Sanity.TestRunnerIsAlive verifies the runner.
Ti folders are empty for now; subsequent tasks populate them.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 0.3: Create the build script skeleton

**Files:**
- Create: `build.ps1`

- [ ] **Step 1: Create `build.ps1`** (Plan A version — refreshes `sts2.dll` and builds + tests; full Godot export is Plan B)

```powershell
# Plan A build script.
# Refreshes the local sts2.dll from the game install, then builds the mod
# csproj and runs the test suite. Plan B will extend this to drive Godot's
# headless export and copy outputs into <game-install>/mods/<id>/.

$ErrorActionPreference = "Stop"

$gameDll = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$srcDll = "src\sts2.dll"

if (-not (Test-Path $gameDll)) {
    throw "sts2.dll not found at $gameDll. Is the game installed?"
}

Copy-Item -Force $gameDll $srcDll
Write-Host "Refreshed $srcDll from game install"

dotnet build src\slay_the_streamer_2.csproj
if ($LASTEXITCODE -ne 0) { throw "main build failed" }

dotnet test tests\slay_the_streamer_2.tests.csproj
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

Write-Host "Plan A build + test cycle: OK"
```

- [ ] **Step 2: Run it once** to verify

Run: `pwsh -File build.ps1`
Expected: ends with `Plan A build + test cycle: OK`.

- [ ] **Step 3: Commit**

```powershell
git add build.ps1
git commit -m @'
plan-a/0.3: build.ps1 skeleton — refresh sts2.dll, build, test

Plan B will extend this with Godot --build-solutions / --export-pack
invocations and an install step.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 1: Internal foundations (4 tasks)

These are the deterministic-testing primitives every other Plan A type depends on. Build them first so subsequent tests can inject `FakeClock`, `FakeTimerScheduler`, `ImmediateDispatcher`, and override `TiLog.Sink`.

### Task 1.1: `IClock` + `SystemClock` + `FakeClock` (TDD)

**Files:**
- Create: `src/Ti/Internal/IClock.cs`
- Create: `src/Ti/Internal/SystemClock.cs`
- Create: `src/Ti/Internal/FakeClock.cs`
- Test: `tests/Internal/FakeClockTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Internal/FakeClockTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class FakeClockTests {
    [Fact]
    public void StartsAtConstructorTime() {
        var t0 = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(t0);
        Assert.Equal(t0, clock.UtcNow);
    }

    [Fact]
    public void AdvanceMovesNowForward() {
        var t0 = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(t0);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(t0 + TimeSpan.FromSeconds(30), clock.UtcNow);
    }

    [Fact]
    public void AdvanceWithNegativeThrows() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeClockTests"`
Expected: build error (`The type or namespace name 'IClock' could not be found` or similar). Failure is correct.

- [ ] **Step 3: Create `src/Ti/Internal/IClock.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Source of "now". Inject so tests can use FakeClock.</summary>
public interface IClock {
    DateTimeOffset UtcNow { get; }
}
```

- [ ] **Step 4: Create `src/Ti/Internal/SystemClock.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Internal;

public sealed class SystemClock : IClock {
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Create `src/Ti/Internal/FakeClock.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Clock under explicit test control.</summary>
public sealed class FakeClock : IClock {
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) { _now = start; }
    public DateTimeOffset UtcNow => _now;

    /// <summary>Advance the clock by <paramref name="delta"/>. Throws on negative.</summary>
    public void Advance(TimeSpan delta) {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "FakeClock.Advance must be non-negative.");
        _now += delta;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeClockTests"`
Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 7: Commit**

```powershell
git add src/Ti/Internal/IClock.cs src/Ti/Internal/SystemClock.cs src/Ti/Internal/FakeClock.cs tests/Internal/FakeClockTests.cs
git commit -m @'
plan-a/1.1: IClock + SystemClock + FakeClock with TDD

FakeClock is the test-injection point for everything that needs UtcNow.
Spec §Architecture.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 1.2: `ITimerScheduler` + `SystemTimerScheduler` + `FakeTimerScheduler` (TDD)

`VoteSession` schedules its close timer and periodic-tally timer through this. `FakeTimerScheduler` advances alongside `FakeClock` so tests can fire timers deterministically without `Thread.Sleep`. (Spec §Architecture; Decisions #12.)

**Files:**
- Create: `src/Ti/Internal/ITimerScheduler.cs`
- Create: `src/Ti/Internal/SystemTimerScheduler.cs`
- Create: `src/Ti/Internal/FakeTimerScheduler.cs`
- Test: `tests/Internal/FakeTimerSchedulerTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Internal/FakeTimerSchedulerTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class FakeTimerSchedulerTests {
    [Fact]
    public void OneShotFiresOnceAtScheduledTime() {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        scheduler.Schedule(TimeSpan.FromSeconds(5), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fired);

        scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);

        scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fired);                                   // one-shot: doesn't refire
    }

    [Fact]
    public void PeriodicFiresRepeatedlyAtInterval() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(7), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(20));              // 7, 14 — fires twice
        Assert.Equal(2, fired);

        scheduler.Advance(TimeSpan.FromSeconds(7));               // 21 — fires a third time
        Assert.Equal(3, fired);
    }

    [Fact]
    public void DisposedHandleStopsFiring() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        var handle = scheduler.SchedulePeriodic(TimeSpan.FromSeconds(7), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(1, fired);

        handle.Dispose();
        scheduler.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void AdvancingDoesNotMoveClockBackward() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(TimeSpan.FromSeconds(-1)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeTimerSchedulerTests"`
Expected: build error about missing `ITimerScheduler` / `FakeTimerScheduler`.

- [ ] **Step 3: Create `src/Ti/Internal/ITimerScheduler.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Schedules one-shot and periodic callbacks. Inject so tests can drive
/// timers deterministically via FakeTimerScheduler instead of relying on
/// real wall-clock System.Threading.Timer.
/// </summary>
public interface ITimerScheduler {
    /// <summary>Fires <paramref name="callback"/> once after <paramref name="delay"/>.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);

    /// <summary>Fires <paramref name="callback"/> every <paramref name="interval"/>, starting at <c>now + interval</c>.</summary>
    IDisposable SchedulePeriodic(TimeSpan interval, Action callback);
}
```

- [ ] **Step 4: Create `src/Ti/Internal/SystemTimerScheduler.cs`**

```csharp
using System;
using System.Threading;

namespace SlayTheStreamer2.Ti.Internal;

public sealed class SystemTimerScheduler : ITimerScheduler {
    public IDisposable Schedule(TimeSpan delay, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(interval, interval);
        return timer;
    }
}
```

- [ ] **Step 5: Create `src/Ti/Internal/FakeTimerScheduler.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Test-controlled scheduler. Fires due callbacks when Advance() is called.</summary>
public sealed class FakeTimerScheduler : ITimerScheduler {
    private readonly FakeClock _clock;
    private readonly List<Entry> _entries = new();

    public FakeTimerScheduler(FakeClock clock) { _clock = clock; }

    public IDisposable Schedule(TimeSpan delay, Action callback) {
        var entry = new Entry { NextFire = _clock.UtcNow + delay, Interval = null, Callback = callback };
        _entries.Add(entry);
        return new Handle(() => _entries.Remove(entry));
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action callback) {
        var entry = new Entry { NextFire = _clock.UtcNow + interval, Interval = interval, Callback = callback };
        _entries.Add(entry);
        return new Handle(() => _entries.Remove(entry));
    }

    /// <summary>Advance the clock and fire any callbacks whose due time falls within the advance.</summary>
    public void Advance(TimeSpan delta) {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "FakeTimerScheduler.Advance must be non-negative.");
        var target = _clock.UtcNow + delta;
        // Walk forward firing in chronological order until we hit `target` with no due entries left.
        while (true) {
            Entry? next = null;
            foreach (var e in _entries)
                if (e.NextFire <= target && (next is null || e.NextFire < next.NextFire))
                    next = e;
            if (next is null) break;
            // advance the clock to that entry's fire time, then fire it
            _clock.Advance(next.NextFire - _clock.UtcNow);
            next.Callback();
            if (next.Interval is { } iv) next.NextFire += iv;
            else _entries.Remove(next);
        }
        // advance any remaining time the wall-clock should reflect even if no more callbacks
        if (_clock.UtcNow < target) _clock.Advance(target - _clock.UtcNow);
    }

    private sealed class Entry {
        public DateTimeOffset NextFire;
        public TimeSpan? Interval;
        public Action Callback = () => {};
    }

    private sealed class Handle : IDisposable {
        private Action? _onDispose;
        public Handle(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeTimerSchedulerTests"`
Expected: `Passed: 4, Failed: 0`.

- [ ] **Step 7: Commit**

```powershell
git add src/Ti/Internal/ITimerScheduler.cs src/Ti/Internal/SystemTimerScheduler.cs src/Ti/Internal/FakeTimerScheduler.cs tests/Internal/FakeTimerSchedulerTests.cs
git commit -m @'
plan-a/1.2: ITimerScheduler + SystemTimerScheduler + FakeTimerScheduler

FakeTimerScheduler advances alongside FakeClock so VoteSession's close
timer and periodic-tally timer can be fired deterministically in tests.
Spec §Decisions #12.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 1.3: `TiLog` static helper with overrideable Sink (TDD)

The single shim site between `Ti/*` and `MegaCrit.Sts2.Core.Logging.Log`. Tests override `Sink` to capture lines for assertions. Token scrubbing is applied here as defense-in-depth on top of `ChatCredentials.ToString` redaction. (Spec §Logging via TiLog; v2.2 rollback from `ITiLogger` interface.)

**Files:**
- Create: `src/Ti/Internal/TiLog.cs`
- Test: `tests/Internal/TiLogTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Internal/TiLogTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class TiLogTests : IDisposable {
    private readonly Action<LogLevel, string, Exception?> _originalSink;
    private readonly List<(LogLevel Level, string Msg, Exception? Ex)> _captured = new();

    public TiLogTests() {
        _originalSink = TiLog.Sink;
        TiLog.Sink = (lvl, msg, ex) => _captured.Add((lvl, msg, ex));
    }

    public void Dispose() => TiLog.Sink = _originalSink;

    [Fact]
    public void InfoForwardsToSinkAtInfoLevel() {
        TiLog.Info("hello");
        Assert.Single(_captured);
        Assert.Equal(LogLevel.Info, _captured[0].Level);
        Assert.Equal("hello", _captured[0].Msg);
        Assert.Null(_captured[0].Ex);
    }

    [Fact]
    public void DebugWarnErrorForwardWithCorrectLevels() {
        TiLog.Debug("d");
        TiLog.Warn("w");
        var ex = new InvalidOperationException("boom");
        TiLog.Error("e", ex);

        Assert.Equal(3, _captured.Count);
        Assert.Equal(LogLevel.Debug, _captured[0].Level);
        Assert.Equal(LogLevel.Warn, _captured[1].Level);
        Assert.Equal(LogLevel.Error, _captured[2].Level);
        Assert.Same(ex, _captured[2].Ex);
    }

    [Fact]
    public void OauthTokensAreScrubbedFromMessages() {
        TiLog.Info("connecting with oauth:abc123def456 to channel #foo");
        Assert.Single(_captured);
        Assert.DoesNotContain("abc123def456", _captured[0].Msg);
        Assert.Contains("oauth:<REDACTED>", _captured[0].Msg);
        Assert.Contains("#foo", _captured[0].Msg);
    }

    [Fact]
    public void OauthTokensAreScrubbedAcrossLevels() {
        TiLog.Warn("oauth:wxyz0987");
        TiLog.Error("token=oauth:wxyz0987 fail", new Exception());
        Assert.DoesNotContain("wxyz0987", _captured[0].Msg);
        Assert.DoesNotContain("wxyz0987", _captured[1].Msg);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TiLogTests"`
Expected: build error about missing `TiLog` / `LogLevel`.

- [ ] **Step 3: Create `src/Ti/Internal/TiLog.cs`**

```csharp
using System;
using System.Text.RegularExpressions;

namespace SlayTheStreamer2.Ti.Internal;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Logging shim for the Ti/* layer. Default Sink forwards to the StS2
/// game logger; tests override Sink to capture lines.
/// </summary>
public static class TiLog {
    private static readonly Regex OauthPattern = new(@"oauth:[A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Receives every log call. Default forwards to MegaCrit.Sts2.Core.Logging.Log;
    /// the default is wired up in ModEntry (Plan B) so the Plan A test environment
    /// gets a no-op default and can override per-test.
    /// </summary>
    public static Action<LogLevel, string, Exception?> Sink { get; set; } = (_, _, _) => { };

    public static void Debug(string msg) => Sink(LogLevel.Debug, Scrub(msg), null);
    public static void Info(string msg) => Sink(LogLevel.Info, Scrub(msg), null);
    public static void Warn(string msg) => Sink(LogLevel.Warn, Scrub(msg), null);
    public static void Error(string msg, Exception? ex = null) => Sink(LogLevel.Error, Scrub(msg), ex);

    private static string Scrub(string msg) => OauthPattern.Replace(msg, "oauth:<REDACTED>");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TiLogTests"`
Expected: `Passed: 4, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Internal/TiLog.cs tests/Internal/TiLogTests.cs
git commit -m @'
plan-a/1.3: TiLog static helper with overrideable Sink

The one shim site between Ti/* and MegaCrit Log. Sink is overrideable
for test capture. Oauth tokens are regex-scrubbed before they reach the
sink as defense-in-depth on top of ChatCredentials.ToString redaction.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 1.4: `IMainThreadDispatcher` + `ImmediateDispatcher` (TDD)

The dispatcher contract decouples `Ti/Chat`, `Ti/Voting`, and `Ti/Internal` from Godot's `CallDeferred`. `ImmediateDispatcher` is the test/headless impl — synchronous pass-through. `GodotMainThreadDispatcher` (Plan B) lives in `Ti/Godot/`. (Spec §Architecture; Decisions #10; Optional Enhancement #10 — `ImmediateDispatcher` is intentionally public.)

**Files:**
- Create: `src/Ti/Internal/IMainThreadDispatcher.cs`
- Create: `src/Ti/Internal/ImmediateDispatcher.cs`
- Test: `tests/Internal/ImmediateDispatcherTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Internal/ImmediateDispatcherTests.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class ImmediateDispatcherTests {
    [Fact]
    public void PostInvokesActionOnCallingThread() {
        var dispatcher = new ImmediateDispatcher();
        var threadSeen = -1;
        dispatcher.Post(() => threadSeen = Thread.CurrentThread.ManagedThreadId);
        Assert.Equal(Thread.CurrentThread.ManagedThreadId, threadSeen);
    }

    [Fact]
    public void PostExecutesSynchronously() {
        var dispatcher = new ImmediateDispatcher();
        var ran = false;
        dispatcher.Post(() => ran = true);
        Assert.True(ran, "action ran synchronously inside Post");
    }

    [Fact]
    public async Task DrainAsyncCompletesImmediately() {
        var dispatcher = new ImmediateDispatcher();
        await dispatcher.DrainAsync();   // never throws, never blocks
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ImmediateDispatcherTests"`
Expected: build error.

- [ ] **Step 3: Create `src/Ti/Internal/IMainThreadDispatcher.cs`**

```csharp
using System;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Marshals callbacks onto a target thread (typically the Godot main thread
/// in production; the calling thread for tests). Decouples Ti/* from Godot.
/// </summary>
public interface IMainThreadDispatcher {
    /// <summary>Queue <paramref name="action"/> for execution on the dispatcher's target thread.</summary>
    void Post(Action action);

    /// <summary>Awaits processing of all currently-queued actions. For ImmediateDispatcher this is a no-op.</summary>
    Task DrainAsync();
}
```

- [ ] **Step 4: Create `src/Ti/Internal/ImmediateDispatcher.cs`**

```csharp
using System;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Synchronous pass-through dispatcher. Executes actions on the calling thread.
/// Used by tests and by non-Godot consumers (the IRC fixture-generator tool, future
/// headless integration tests). Public on purpose — see Optional Enhancement #10.
/// </summary>
public sealed class ImmediateDispatcher : IMainThreadDispatcher {
    public void Post(Action action) => action();
    public Task DrainAsync() => Task.CompletedTask;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ImmediateDispatcherTests"`
Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 6: Commit**

```powershell
git add src/Ti/Internal/IMainThreadDispatcher.cs src/Ti/Internal/ImmediateDispatcher.cs tests/Internal/ImmediateDispatcherTests.cs
git commit -m @'
plan-a/1.4: IMainThreadDispatcher + ImmediateDispatcher

ImmediateDispatcher is the synchronous test/headless impl. The Godot
impl lives in Ti/Godot/ in Plan B. Public visibility per Optional
Enhancement #10 so non-Godot consumers can use it directly.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 2: Chat data types (5 tasks)

These are the data shapes consumed by `VoteSession` and produced by `TwitchIrcChatService`. No behaviour beyond record-equality and a couple of TDD'd validation methods. (Spec §`ChatService` (lower tier).)

### Task 2.1: Enums and event args (no tests — just declarations)

These are simple data declarations; tests would tautologically restate the enum values. Skip TDD for these; they'll be exercised by the tests in later tasks.

**Files:**
- Create: `src/Ti/Chat/ChatConnectionState.cs`
- Create: `src/Ti/Chat/ChatConnectionChangedEventArgs.cs`
- Create: `src/Ti/Chat/OutgoingMessagePriority.cs`

- [ ] **Step 1: Create `src/Ti/Chat/ChatConnectionState.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Chat;

public enum ChatConnectionState {
    Disconnected,
    Connecting,
    ConnectedReadOnly,           // anonymous justinfan
    ConnectedReadWrite,          // authenticated
    Reconnecting,
    AuthenticationFailed,        // terminal — no retry
    JoinFailed,                  // banned / channel doesn't exist — no retry
    Disposed,
}
```

- [ ] **Step 2: Create `src/Ti/Chat/ChatConnectionChangedEventArgs.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Chat;

public sealed record ChatConnectionChangedEventArgs(
    ChatConnectionState OldState,
    ChatConnectionState NewState,
    string? Reason);
```

- [ ] **Step 3: Create `src/Ti/Chat/OutgoingMessagePriority.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Chat;

/// <summary>Priority for the outgoing send queue. Close > Open > Periodic. Plan A defines the enum;
/// the queue implementation that uses it lives in Ti/Chat/Internal/OutgoingMessageQueue.cs.</summary>
public enum OutgoingMessagePriority {
    Low,        // periodic tally
    Normal,     // open receipt
    High,       // close receipt
}
```

- [ ] **Step 4: Verify the project still compiles**

Run: `dotnet build src/slay_the_streamer_2.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/ChatConnectionState.cs src/Ti/Chat/ChatConnectionChangedEventArgs.cs src/Ti/Chat/OutgoingMessagePriority.cs
git commit -m @'
plan-a/2.1: chat enums and event args

ChatConnectionState replaces bool IsConnected with explicit terminal
states (AuthenticationFailed, JoinFailed) per spec Decision #14.
OutgoingMessagePriority drives the send-queue priority (Plan A 8.x).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2.2: `ChatCredentials` with oauth normalisation + redaction (TDD)

Accepts both `oauth:abc` and bare `abc`; normalises username to lowercase; `ToString` never leaks the token. (Spec §ChatService implementations; Optional Enhancement #11.)

**Files:**
- Create: `src/Ti/Chat/ChatCredentials.cs`
- Test: `tests/Chat/ChatCredentialsTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Chat/ChatCredentialsTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatCredentialsTests {
    [Fact]
    public void StoresUsernameLowercased() {
        var c = new ChatCredentials("Surfinite", "abc123");
        Assert.Equal("surfinite", c.Username);
    }

    [Fact]
    public void StripsOauthPrefix() {
        var c = new ChatCredentials("u", "oauth:abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void StripsOauthPrefixCaseInsensitive() {
        var c = new ChatCredentials("u", "OAuth:abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void AcceptsBareTokenUnchanged() {
        var c = new ChatCredentials("u", "abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void NullUsernameThrows() {
        Assert.Throws<ArgumentNullException>(() => new ChatCredentials(null!, "abc"));
    }

    [Fact]
    public void NullTokenThrows() {
        Assert.Throws<ArgumentNullException>(() => new ChatCredentials("u", null!));
    }

    [Fact]
    public void ToStringRedactsToken() {
        var c = new ChatCredentials("Surfinite", "oauth:secret_token_12345");
        var s = c.ToString();
        Assert.DoesNotContain("secret_token_12345", s);
        Assert.Contains("REDACTED", s);
        Assert.Contains("surfinite", s);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ChatCredentialsTests"`
Expected: build error about missing `ChatCredentials`.

- [ ] **Step 3: Create `src/Ti/Chat/ChatCredentials.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Twitch chat login credentials. Stores token without the "oauth:" prefix;
/// TwitchIrcChatService prepends it on PASS. ToString redacts the token.
/// </summary>
public sealed class ChatCredentials {
    public string Username { get; }
    public string OauthToken { get; }

    public ChatCredentials(string username, string oauthToken) {
        if (username is null) throw new ArgumentNullException(nameof(username));
        if (oauthToken is null) throw new ArgumentNullException(nameof(oauthToken));

        Username = username.ToLowerInvariant();
        OauthToken = oauthToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)
            ? oauthToken.Substring("oauth:".Length)
            : oauthToken;
    }

    public override string ToString() => $"ChatCredentials[{Username}, oauth:<REDACTED>]";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ChatCredentialsTests"`
Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/ChatCredentials.cs tests/Chat/ChatCredentialsTests.cs
git commit -m @'
plan-a/2.2: ChatCredentials with oauth normalisation + ToString redaction

Username stored lowercased; oauth:-prefix stripped if present (Optional
Enhancement #11). ToString never leaks the token (defense-in-depth on
top of TiLog scrubbing).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2.3: `ChatMessage` record with `VoterKey` derivation

The record itself is straightforward; the only logic worth a test is the `VoterKey` fallback when `UserId` is null. (Spec §ChatMessage; Decisions #14 — Reviewer 3/4/5/6 fix.)

**Files:**
- Create: `src/Ti/Chat/ChatMessage.cs`
- Test: extend `tests/Chat/ChatCredentialsTests.cs`-style — but `ChatMessage` is pure data, so write a small dedicated test class.

- [ ] **Step 1: Create the failing test**

`tests/Chat/ChatMessageTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatMessageTests {
    [Fact]
    public void VoterKeyIsUserIdWhenPresent() {
        var msg = new ChatMessage(
            UserId: "12345",
            Login: "alice",
            DisplayName: "Alice",
            Text: "hi",
            ReceivedAt: DateTimeOffset.UtcNow,
            IsSubscriber: false, IsModerator: false, IsVip: false);
        Assert.Equal("12345", msg.VoterKey);
    }

    [Fact]
    public void VoterKeyFallsBackToLoginWhenUserIdNull() {
        var msg = new ChatMessage(
            UserId: null,
            Login: "alice",
            DisplayName: "Alice",
            Text: "hi",
            ReceivedAt: DateTimeOffset.UtcNow,
            IsSubscriber: false, IsModerator: false, IsVip: false);
        Assert.Equal("login:alice", msg.VoterKey);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ChatMessageTests"`
Expected: build error.

- [ ] **Step 3: Create `src/Ti/Chat/ChatMessage.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// One incoming chat message after parsing. UserId is null when the IRC client
/// is connected without `twitch.tv/tags` capability or for messages from
/// untagged sources. VoterKey is the stable identifier VoteSession tallies on.
/// </summary>
public sealed record ChatMessage(
    string? UserId,
    string Login,
    string DisplayName,
    string Text,
    DateTimeOffset ReceivedAt,
    bool IsSubscriber,
    bool IsModerator,
    bool IsVip) {
    public string VoterKey => UserId ?? $"login:{Login}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ChatMessageTests"`
Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/ChatMessage.cs tests/Chat/ChatMessageTests.cs
git commit -m @'
plan-a/2.3: ChatMessage record with VoterKey fallback

VoterKey defaults to UserId; falls back to "login:<login>" when the
IRC client is in anonymous/no-tags mode. VoteSession tallies on this
key. Spec §ChatMessage; Decisions #14.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2.4: `IChatService` interface

The contract that `VoteSession` consumes. The real impl (`TwitchIrcChatService`) is Plan B; Plan A's `FakeChatService` (Task 3.1) is the only impl Plan A's tests use.

**Files:**
- Create: `src/Ti/Chat/IChatService.cs`

- [ ] **Step 1: Create `src/Ti/Chat/IChatService.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Twitch chat I/O. The real impl is TwitchIrcChatService (Plan B); Plan A
/// uses FakeChatService for all tests.
/// </summary>
public interface IChatService : IDisposable {
    ChatConnectionState State { get; }
    bool IsConnected { get; }              // convenience: any of ConnectedReadOnly/ConnectedReadWrite/Reconnecting
    bool CanSend { get; }                  // false in Anonymous/Disconnected/AuthFailed/JoinFailed/Disposed
    DateTimeOffset? LastMessageReceivedAt { get; }
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
    void Disconnect();

    Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Verify project compiles**

Run: `dotnet build src/slay_the_streamer_2.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```powershell
git add src/Ti/Chat/IChatService.cs
git commit -m @'
plan-a/2.4: IChatService interface

The contract VoteSession consumes; TwitchIrcChatService (Plan B) is
the real impl; FakeChatService (Plan A 3.1) is the test impl.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 3: `FakeChatService` (1 task)

### Task 3.1: `FakeChatService` (TDD)

In-memory `IChatService` with `Inject(ChatMessage)` to deliver messages synchronously to subscribers, and `SentMessages` to assert on outgoing sends. No network. (Spec §ChatService implementations.)

**Files:**
- Create: `src/Ti/Chat/FakeChatService.cs`
- Test: `tests/Chat/FakeChatServiceTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Chat/FakeChatServiceTests.cs`:
```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class FakeChatServiceTests {
    private static ChatMessage Msg(string login = "alice", string text = "hi", string? userId = "1") =>
        new(userId, login, login, text, DateTimeOffset.UtcNow, false, false, false);

    [Fact]
    public void StartsDisconnected() {
        var chat = new FakeChatService();
        Assert.Equal(ChatConnectionState.Disconnected, chat.State);
        Assert.False(chat.IsConnected);
        Assert.False(chat.CanSend);
    }

    [Fact]
    public async Task ConnectMovesToConnectedReadWriteWithCreds() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, chat.State);
        Assert.True(chat.IsConnected);
        Assert.True(chat.CanSend);
    }

    [Fact]
    public async Task ConnectMovesToConnectedReadOnlyWithoutCreds() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", creds: null);
        Assert.Equal(ChatConnectionState.ConnectedReadOnly, chat.State);
        Assert.True(chat.IsConnected);
        Assert.False(chat.CanSend);
    }

    [Fact]
    public async Task InjectRaisesMessageReceivedSynchronously() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo");
        ChatMessage? seen = null;
        chat.MessageReceived += (_, m) => seen = m;

        var injected = Msg();
        chat.Inject(injected);
        Assert.Same(injected, seen);
    }

    [Fact]
    public async Task SendMessageAsyncRecordsAtCorrectPriority() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        await chat.SendMessageAsync("open", OutgoingMessagePriority.Normal);
        await chat.SendMessageAsync("tally", OutgoingMessagePriority.Low);
        await chat.SendMessageAsync("close", OutgoingMessagePriority.High);

        Assert.Equal(3, chat.SentMessages.Count);
        Assert.Equal(("open", OutgoingMessagePriority.Normal), (chat.SentMessages[0].Text, chat.SentMessages[0].Priority));
        Assert.Equal(("tally", OutgoingMessagePriority.Low), (chat.SentMessages[1].Text, chat.SentMessages[1].Priority));
        Assert.Equal(("close", OutgoingMessagePriority.High), (chat.SentMessages[2].Text, chat.SentMessages[2].Priority));
    }

    [Fact]
    public async Task SendInAnonymousModeFailsTask() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", creds: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendMessageAsync("hi"));
    }

    [Fact]
    public async Task DisconnectMovesToDisconnectedAndFiresEvent() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        ChatConnectionChangedEventArgs? lastEvt = null;
        chat.ConnectionStateChanged += (_, e) => lastEvt = e;

        chat.Disconnect();
        Assert.Equal(ChatConnectionState.Disconnected, chat.State);
        Assert.NotNull(lastEvt);
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, lastEvt!.OldState);
        Assert.Equal(ChatConnectionState.Disconnected, lastEvt.NewState);
    }

    [Fact]
    public async Task LastMessageReceivedAtUpdatesOnInject() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo");
        Assert.Null(chat.LastMessageReceivedAt);

        var t = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        chat.Inject(Msg() with { ReceivedAt = t });
        Assert.Equal(t, chat.LastMessageReceivedAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeChatServiceTests"`
Expected: build error about missing `FakeChatService`.

- [ ] **Step 3: Create `src/Ti/Chat/FakeChatService.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// In-memory IChatService for tests and dev. Inject() delivers messages
/// synchronously to subscribers; SentMessages records every outgoing send.
/// </summary>
public sealed class FakeChatService : IChatService {
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private readonly List<SentMessage> _sent = new();

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.ConnectedReadWrite or
        ChatConnectionState.Reconnecting;
    public bool CanSend => _state == ChatConnectionState.ConnectedReadWrite;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>Outgoing messages recorded for assertions.</summary>
    public IReadOnlyList<SentMessage> SentMessages => _sent;

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        SetState(creds is null ? ChatConnectionState.ConnectedReadOnly : ChatConnectionState.ConnectedReadWrite);
        return Task.CompletedTask;
    }

    public void Disconnect() => SetState(ChatConnectionState.Disconnected);

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) {
        if (!CanSend)
            return Task.FromException(new InvalidOperationException(
                $"Cannot send while State = {_state} (CanSend == false)"));
        _sent.Add(new SentMessage(text, priority));
        return Task.CompletedTask;
    }

    /// <summary>Deliver a message synchronously to MessageReceived subscribers.</summary>
    public void Inject(ChatMessage message) {
        LastMessageReceivedAt = message.ReceivedAt;
        MessageReceived?.Invoke(this, message);
    }

    /// <summary>Force the service into a specific state (e.g. simulate auth failure or mid-vote disconnect).</summary>
    public void SimulateState(ChatConnectionState state, string? reason = null) => SetState(state, reason);

    public void Dispose() => SetState(ChatConnectionState.Disposed);

    private void SetState(ChatConnectionState next, string? reason = null) {
        if (_state == next) return;
        var old = _state;
        _state = next;
        ConnectionStateChanged?.Invoke(this, new ChatConnectionChangedEventArgs(old, next, reason));
    }

    public sealed record SentMessage(string Text, OutgoingMessagePriority Priority);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~FakeChatServiceTests"`
Expected: `Passed: 8, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/FakeChatService.cs tests/Chat/FakeChatServiceTests.cs
git commit -m @'
plan-a/3.1: FakeChatService

In-memory IChatService for tests and the IRC fixture-generator tool.
Inject() delivers messages synchronously to subscribers; SentMessages
records every outgoing send for assertions; SimulateState() lets tests
exercise mid-vote disconnect, auth-failure, etc.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 4: Voting types and `EnglishReceipts` (3 tasks)

### Task 4.1: Voting enums, policies, options (no separate tests — exercised by VoteSession in Phase 5)

These are simple data declarations. `VoteOption` has an internal constructor (Optional Enhancement #15) so only `VoteCoordinator` builds the list. (Spec §VoteSession types; Decisions #14 (0-indexed); Optional Enhancement #15.)

**Files:**
- Create: `src/Ti/Voting/VoteOption.cs`
- Create: `src/Ti/Voting/VoteSessionState.cs`
- Create: `src/Ti/Voting/VoteParsingPolicy.cs`
- Create: `src/Ti/Voting/VoteReceiptPolicy.cs`
- Create: `src/Ti/Voting/ReceiptKind.cs`

- [ ] **Step 1: Create `src/Ti/Voting/VoteOption.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// One option in a vote. Index is 0-based and equals the option's position in
/// the Options list. Constructor is internal — only VoteCoordinator builds these,
/// keeping Index and position in sync.
/// </summary>
public sealed record VoteOption {
    public int Index { get; }
    public string Label { get; }
    internal VoteOption(int index, string label) { Index = index; Label = label; }
}
```

- [ ] **Step 2: Create `src/Ti/Voting/VoteSessionState.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Voting;

public enum VoteSessionState {
    Open,         // accepting votes
    Closing,      // duration elapsed or CloseNow() called; computing winner + sending close receipt
    Closed,       // WinnerIndex set; subscribers notified
    Cancelled,    // Cancel() called; no winner; awaiters cancelled
    Disposed,
}
```

- [ ] **Step 3: Create `src/Ti/Voting/VoteParsingPolicy.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Voting;

/// <summary>Toggles for the vote-command parser. Default accepts both `#N` and `!N`.</summary>
public sealed record VoteParsingPolicy(
    bool AcceptHashCommands = true,
    bool AcceptBangCommands = true) {
    public static VoteParsingPolicy Default => new();
    public static VoteParsingPolicy HashOnly => new(true, false);
}
```

- [ ] **Step 4: Create `src/Ti/Voting/VoteReceiptPolicy.cs`**

```csharp
using System;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Controls receipt cadence/announcements.
/// PeriodicTallyEvery semantics: null = adaptive (max(5s, duration/5));
/// TimeSpan.Zero = no periodic tally; positive value = fixed cadence.
/// </summary>
public sealed record VoteReceiptPolicy(
    bool AnnounceOnOpen = true,
    TimeSpan? PeriodicTallyEvery = null,
    bool AnnounceOnClose = true) {
    public static VoteReceiptPolicy Default => new();
    public static VoteReceiptPolicy Silent => new(false, TimeSpan.Zero, false);
    public static VoteReceiptPolicy WithFixedCadence(TimeSpan cadence) => new(true, cadence, true);
}
```

- [ ] **Step 5: Create `src/Ti/Voting/ReceiptKind.cs`**

```csharp
namespace SlayTheStreamer2.Ti.Voting;

/// <summary>Which receipt the formatter is being asked to render.</summary>
public enum ReceiptKind { Open, PeriodicTally, Close }
```

- [ ] **Step 6: Verify project compiles**

Run: `dotnet build src/slay_the_streamer_2.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```powershell
git add src/Ti/Voting/VoteOption.cs src/Ti/Voting/VoteSessionState.cs src/Ti/Voting/VoteParsingPolicy.cs src/Ti/Voting/VoteReceiptPolicy.cs src/Ti/Voting/ReceiptKind.cs
git commit -m @'
plan-a/4.1: voting data types and policies

VoteOption (internal ctor; 0-indexed), VoteSessionState (Open/Closing
/Closed/Cancelled/Disposed), VoteParsingPolicy (default accepts #N
and !N), VoteReceiptPolicy (adaptive cadence default + Silent + fixed
preset), ReceiptKind enum.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 4.2: `EnglishReceipts` static helper (TDD)

Renders all six receipt formats (open, periodic, close-winner, close-2-tie, close-3+-tie, close-no-vote, close-disconnect-gap). Pure function from `VoteSession` to string; testable directly. (Spec §Receipts; Optional Enhancement #13 distinguishes 2-tie vs 3+-tie.)

Note: this task constructs `VoteSession` instances for input, but `VoteSession` itself is built in Phase 5. To break the dependency, use a small **`VoteSnapshot`** test-double type that exposes the data `EnglishReceipts` actually reads. We'll wire `EnglishReceipts` to take a `VoteSnapshot` input; later `VoteCoordinator` calls `EnglishReceipts.FormatX(session.Snapshot())`.

**Files:**
- Create: `src/Ti/Voting/VoteSnapshot.cs`
- Create: `src/Ti/Voting/EnglishReceipts.cs`
- Test: `tests/Voting/EnglishReceiptsTests.cs`

- [ ] **Step 1: Create `src/Ti/Voting/VoteSnapshot.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Read-only view of a vote at a moment in time. EnglishReceipts and the UI
/// overlay both consume this; VoteSession produces it. Decoupling the formatter
/// from VoteSession makes EnglishReceipts unit-testable without spinning up
/// a session.
/// </summary>
public sealed record VoteSnapshot(
    string Id,
    string Label,
    IReadOnlyList<VoteOption> Options,
    TimeSpan Duration,
    TimeSpan TimeRemaining,
    IReadOnlyDictionary<int, int> Tallies,
    VoteSessionState State,
    int? WinnerIndex,
    int? RandomTieAmong,                  // when WinnerIndex was picked from a tie, how many options were tied
    bool NoVotesReceived,                 // true if WinnerIndex was picked from all options because zero votes came in
    TimeSpan DisconnectGap                // total time chat was offline during the vote (TimeSpan.Zero if none)
);
```

- [ ] **Step 2: Create the failing test**

`tests/Voting/EnglishReceiptsTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class EnglishReceiptsTests {
    private static VoteSnapshot Snap(
        VoteSessionState state = VoteSessionState.Open,
        int? winner = null,
        int? tieAmong = null,
        bool noVotes = false,
        TimeSpan? remaining = null,
        TimeSpan? disconnectGap = null,
        IReadOnlyDictionary<int, int>? tallies = null,
        IReadOnlyList<VoteOption>? options = null) {
        var opts = options ?? new List<VoteOption> {
            new(0, "Bash"),
            new(1, "Defend"),
            new(2, "Strike"),
        };
        var tlies = tallies ?? new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
        return new VoteSnapshot(
            "card-reward-X", "card reward",
            opts, TimeSpan.FromSeconds(30), remaining ?? TimeSpan.FromSeconds(30),
            tlies, state, winner, tieAmong, noVotes, disconnectGap ?? TimeSpan.Zero);
    }

    [Fact]
    public void OpenIncludesLabelOptionsAndDuration() {
        var s = Snap();
        var text = EnglishReceipts.FormatOpen(s);
        Assert.Contains("card reward", text);
        Assert.Contains("0", text);
        Assert.Contains("1", text);
        Assert.Contains("2", text);
        Assert.Contains("30s", text);
    }

    [Fact]
    public void PeriodicShowsTalliesAndRemaining() {
        var s = Snap(
            tallies: new Dictionary<int, int> { [0] = 12, [1] = 8, [2] = 3 },
            remaining: TimeSpan.FromSeconds(15));
        var text = EnglishReceipts.FormatPeriodicTally(s);
        Assert.Contains("0=12", text);
        Assert.Contains("1=8", text);
        Assert.Contains("2=3", text);
        Assert.Contains("15s", text);
    }

    [Fact]
    public void CloseWinnerSaysChatChose() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("Chat chose", text);
        Assert.Contains("1", text);
        Assert.Contains("Defend", text);
    }

    [Fact]
    public void CloseTwoWayTieMentionsBetween() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1, tieAmong: 2);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("Tie", text);
        Assert.Contains("between", text);
        Assert.Contains("Defend", text);
    }

    [Fact]
    public void CloseThreePlusWayTieUsesDistinctFormat() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1, tieAmong: 3);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("3-way tie", text);
        Assert.Contains("Defend", text);
        Assert.DoesNotContain("between", text);   // distinct from 2-way format
    }

    [Fact]
    public void CloseNoVotesAnnouncesRandomPick() {
        var s = Snap(state: VoteSessionState.Closed, winner: 0, noVotes: true);
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("No votes", text);
        Assert.Contains("Bash", text);
        Assert.Contains("randomly", text);
    }

    [Fact]
    public void CloseWithDisconnectGapMentionsOfflineSeconds() {
        var s = Snap(state: VoteSessionState.Closed, winner: 1, disconnectGap: TimeSpan.FromSeconds(8));
        var text = EnglishReceipts.FormatClose(s);
        Assert.Contains("8s", text);
        Assert.Contains("offline", text);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~EnglishReceiptsTests"`
Expected: build error about missing `VoteSnapshot` / `EnglishReceipts`.

- [ ] **Step 4: Create `src/Ti/Voting/EnglishReceipts.cs`**

```csharp
using System;
using System.Linq;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Default English receipt text. Pure functions from VoteSnapshot to string.
/// Future i18n: add a peer SpanishReceipts.cs (etc.) and pass a delegate to
/// VoteCoordinator.Start to override.
/// </summary>
public static class EnglishReceipts {
    public static string FormatOpen(VoteSnapshot s) {
        var numbers = string.Join(", ", s.Options.Select(o => o.Index.ToString()));
        return $"Vote: {s.Label}! Type {numbers} — {(int)s.Duration.TotalSeconds}s left.";
    }

    public static string FormatPeriodicTally(VoteSnapshot s) {
        var counts = string.Join(" ", s.Options.Select(o =>
            $"{o.Index}={(s.Tallies.TryGetValue(o.Index, out var c) ? c : 0)}"));
        return $"Vote: {counts}, {(int)s.TimeRemaining.TotalSeconds}s left.";
    }

    public static string FormatClose(VoteSnapshot s) {
        if (s.WinnerIndex is not int winnerIdx)
            return $"Vote: {s.Label} closed without a winner.";   // shouldn't happen on natural close

        var winnerLabel = s.Options.First(o => o.Index == winnerIdx).Label;
        string body;

        if (s.NoVotesReceived) {
            body = $"No votes received — chat got {winnerIdx}: {winnerLabel} randomly.";
        } else if (s.RandomTieAmong is int tied) {
            if (tied >= 3) {
                body = $"{tied}-way tie! Chat chose {winnerIdx}: {winnerLabel} randomly.";
            } else {
                // tied == 2: name the two tied options
                var tiedLabels = string.Join(" and ", s.Tallies
                    .Where(kv => kv.Value == s.Tallies.Values.Max())
                    .Select(kv => $"{kv.Key} {s.Options.First(o => o.Index == kv.Key).Label}"));
                body = $"Tie between {tiedLabels} — chat chose {winnerIdx}: {winnerLabel} randomly.";
            }
        } else {
            body = $"Chat chose {winnerIdx}: {winnerLabel}.";
        }

        if (s.DisconnectGap > TimeSpan.Zero) {
            body = body.TrimEnd('.') + $" (chat was offline {(int)s.DisconnectGap.TotalSeconds}s during voting).";
        }
        return body;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~EnglishReceiptsTests"`
Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 6: Commit**

```powershell
git add src/Ti/Voting/VoteSnapshot.cs src/Ti/Voting/EnglishReceipts.cs tests/Voting/EnglishReceiptsTests.cs
git commit -m @'
plan-a/4.2: EnglishReceipts static helper + VoteSnapshot

VoteSnapshot is a read-only view of a vote at a moment in time;
EnglishReceipts renders all six formats (open, periodic, close-winner,
close-2-tie, close-3+-tie, close-no-vote, close-disconnect-gap) as
pure functions. Decoupling from VoteSession lets EnglishReceipts be
unit-tested without spinning up a session.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 5: `VoteSession` (8 tasks)

The voting engine. Built incrementally — each task adds one capability and its tests, all driven through `FakeChatService` + `FakeClock` + `FakeTimerScheduler` + `ImmediateDispatcher` + a seeded `Random`. (Spec §VoteSession.)

**Test helper** — most tests share the same setup. To avoid 200 lines of repetition, all `VoteSessionTests` extend a base class with the common fakes:

### Task 5.0: Test base class (no production code)

**Files:**
- Create: `tests/Voting/VoteSessionTestBase.cs`

- [ ] **Step 1: Create `tests/Voting/VoteSessionTestBase.cs`**

```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Tests.Voting;

public abstract class VoteSessionTestBase {
    protected readonly FakeClock Clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    protected readonly FakeTimerScheduler Scheduler;
    protected readonly ImmediateDispatcher Dispatcher = new();
    protected readonly FakeChatService Chat = new();
    protected readonly Random Rng = new(42);   // seeded so tie-break tests are deterministic

    protected VoteSessionTestBase() {
        Scheduler = new FakeTimerScheduler(Clock);
        Chat.ConnectAsync("test", new ChatCredentials("bot", "abc")).GetAwaiter().GetResult();
    }

    protected VoteSession StartVote(
        string label = "card reward",
        TimeSpan? duration = null,
        VoteParsingPolicy? parsing = null,
        VoteReceiptPolicy? receipts = null,
        params string[] options) {
        var opts = options.Length == 0 ? new[] { "Bash", "Defend", "Strike" } : options;
        var optionList = new List<VoteOption>();
        for (int i = 0; i < opts.Length; i++) optionList.Add(new VoteOption(i, opts[i]));

        return new VoteSession(
            id: $"{label}-test",
            label: label,
            options: optionList,
            duration: duration ?? TimeSpan.FromSeconds(30),
            chat: Chat,
            clock: Clock,
            scheduler: Scheduler,
            dispatcher: Dispatcher,
            random: Rng,
            parsingPolicy: parsing ?? VoteParsingPolicy.Default,
            receiptPolicy: receipts ?? VoteReceiptPolicy.Default,
            formatReceipt: null);
    }

    protected void Inject(string user, string text, string? userId = null) {
        userId ??= $"id-{user}";
        Chat.Inject(new ChatMessage(
            userId, user, user, text, Clock.UtcNow, false, false, false));
    }
}
```

- [ ] **Step 2: Commit (no test runs yet — base class only)**

```powershell
git add tests/Voting/VoteSessionTestBase.cs
git commit -m @'
plan-a/5.0: test base class for VoteSession suite

Encapsulates the shared FakeClock/Scheduler/Dispatcher/Chat/Rng setup
so individual tests stay focused on the behaviour they're asserting.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.1: VoteSession scaffolding (TDD)

Constructor + validation + initial state + `Snapshot()`. No vote-handling yet. (Spec §Validation.)

**Files:**
- Create: `src/Ti/Voting/VoteSession.cs`
- Create: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Voting/VoteSessionTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteSessionTests : VoteSessionTestBase {
    [Fact]
    public void NewSessionStartsInOpenState() {
        var s = StartVote();
        Assert.Equal(VoteSessionState.Open, s.State);
        Assert.Null(s.WinnerIndex);
    }

    [Fact]
    public void OptionsExposed_ZeroIndexed_PositionMatchesIndex() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(3, s.Options.Count);
        Assert.Equal(0, s.Options[0].Index);
        Assert.Equal("A", s.Options[0].Label);
        Assert.Equal(2, s.Options[2].Index);
    }

    [Fact]
    public void TalliesStartAtZeroForEveryOption() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void TimeRemainingStartsAtDuration() {
        var s = StartVote(duration: TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), s.TimeRemaining);
    }

    [Fact]
    public void TimeRemainingDecreasesAsClockAdvances() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(23), s.TimeRemaining);
    }

    [Fact]
    public void SnapshotMirrorsCurrentState() {
        var s = StartVote();
        var snap = s.Snapshot();
        Assert.Equal(s.Id, snap.Id);
        Assert.Equal(s.Label, snap.Label);
        Assert.Equal(s.State, snap.State);
        Assert.Equal(s.Options.Count, snap.Options.Count);
    }

    [Fact]
    public void EmptyOptionsThrow() {
        Assert.Throws<ArgumentException>(() => StartVote(options: System.Array.Empty<string>()));
    }

    [Fact]
    public void DurationLessThanOneSecondThrows() {
        Assert.Throws<ArgumentException>(() => StartVote(duration: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void MoreThanTenOptionsThrows() {
        var eleven = new string[11];
        for (int i = 0; i < 11; i++) eleven[i] = $"opt{i}";
        Assert.Throws<ArgumentException>(() => StartVote(options: eleven));
    }

    [Fact]
    public void EmptyOrWhitespaceLabelThrows() {
        var bad = new[] { "  ", "" };
        Assert.Throws<ArgumentException>(() => StartVote(options: bad));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: build error (`VoteSession` does not exist).

- [ ] **Step 3: Create `src/Ti/Voting/VoteSession.cs`** (Task 5.1 minimal version — later tasks extend it)

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

public sealed class VoteSession : IDisposable {
    // Dependencies (held for use by later tasks; some unused in 5.1)
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;
    private readonly VoteParsingPolicy _parsing;
    private readonly VoteReceiptPolicy _receipts;
    private readonly Func<VoteSnapshot, ReceiptKind, string>? _formatReceipt;

    private readonly DateTimeOffset _openedAt;
    private readonly Dictionary<int, int> _tallies;
    private VoteSessionState _state = VoteSessionState.Open;

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public VoteSessionState State => _state;
    public int? WinnerIndex { get; private set; }
    public TimeSpan TimeRemaining => MaxZero(_openedAt + Duration - _clock.UtcNow);

    public IReadOnlyDictionary<int, int> Tallies => new Dictionary<int, int>(_tallies);

    internal VoteSession(
        string id,
        string label,
        IReadOnlyList<VoteOption> options,
        TimeSpan duration,
        IChatService chat,
        IClock clock,
        ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher,
        Random random,
        VoteParsingPolicy parsingPolicy,
        VoteReceiptPolicy receiptPolicy,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt) {

        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        if (options is null || options.Count == 0) throw new ArgumentException("at least one option required", nameof(options));
        if (options.Count > 10) throw new ArgumentException("max 10 options (0..9)", nameof(options));
        for (int i = 0; i < options.Count; i++) {
            if (string.IsNullOrWhiteSpace(options[i].Label))
                throw new ArgumentException($"option {i} has empty label", nameof(options));
            if (options[i].Index != i)
                throw new ArgumentException($"option {i} has wrong Index ({options[i].Index})", nameof(options));
        }
        if (duration < TimeSpan.FromSeconds(1)) throw new ArgumentException("duration must be >= 1s", nameof(duration));

        Id = id; Label = label; Options = options; Duration = duration;
        _chat = chat; _clock = clock; _scheduler = scheduler; _dispatcher = dispatcher;
        _random = random; _parsing = parsingPolicy; _receipts = receiptPolicy;
        _formatReceipt = formatReceipt;

        _openedAt = clock.UtcNow;
        _tallies = options.ToDictionary(o => o.Index, _ => 0);
    }

    public VoteSnapshot Snapshot() => new(
        Id: Id, Label: Label, Options: Options,
        Duration: Duration, TimeRemaining: TimeRemaining,
        Tallies: new Dictionary<int, int>(_tallies),
        State: _state, WinnerIndex: WinnerIndex,
        RandomTieAmong: null, NoVotesReceived: false,
        DisconnectGap: TimeSpan.Zero);

    public void Dispose() { _state = VoteSessionState.Disposed; }

    private static TimeSpan MaxZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 10, Failed: 0`. (10 tests, all from Task 5.1.)

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.1: VoteSession scaffolding — ctor, validation, initial state, Snapshot

10 tests covering: starts Open, 0-indexed Options, zero-initialized
Tallies, TimeRemaining decreases as clock advances, Snapshot mirrors
state, validation throws on empty/oversized options/short duration/
empty labels.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.2: Vote parsing + tally (latest-wins, dedup, out-of-range)

Subscribe to `IChatService.MessageReceived`; parse `^[#!]?(\d+)(?:\s|$)` (toggleable via `VoteParsingPolicy`); update `_tallies` with latest-wins semantics; fire `TallyChanged` only on actual change. (Spec §Command parsing; §Tally rules.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs` (append tests)

- [ ] **Step 1: Append tests**

Append to `tests/Voting/VoteSessionTests.cs` inside the `VoteSessionTests` class:
```csharp
    [Fact]
    public void HashVoteIsCounted() {
        var s = StartVote();
        Inject("alice", "#1");
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void BareNumberVoteIsCounted() {
        var s = StartVote();
        Inject("alice", "1");
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void BangVoteIsCountedByDefault() {
        var s = StartVote();
        Inject("alice", "!1");
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void BangVoteIsRejectedWhenPolicyDisablesIt() {
        var s = StartVote(parsing: VoteParsingPolicy.HashOnly);
        Inject("alice", "!1");
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void LatestVoteFromSameUserReplacesEarlier() {
        var s = StartVote();
        Inject("alice", "#1");
        Inject("alice", "#2");
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(1, s.Tallies[2]);
    }

    [Fact]
    public void OutOfRangeIndexIsIgnored() {
        var s = StartVote();
        Inject("alice", "#7");          // only 0..2 valid
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void NonAnchoredMatchIsIgnored() {
        var s = StartVote();
        Inject("alice", "lol #1");      // not at start
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void OrdinalsLikeOneStAreIgnored() {
        var s = StartVote();
        Inject("alice", "1st time voter");
        Inject("bob", "1.5 sec brb");
        Assert.Equal(0, s.Tallies[1]);
    }

    [Fact]
    public void TallyChangedFiresOnVoteChange() {
        var s = StartVote();
        var fired = 0;
        s.TallyChanged += (_, _) => fired++;
        Inject("alice", "#1");
        Assert.Equal(1, fired);
        Inject("alice", "#1");          // same vote — no change
        Assert.Equal(1, fired);
        Inject("alice", "#2");          // change
        Assert.Equal(2, fired);
    }

    [Fact]
    public void DifferentUsersAccumulate() {
        var s = StartVote();
        Inject("alice", "#0");
        Inject("bob", "#0");
        Inject("carol", "#1");
        Assert.Equal(2, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
    }

    [Fact]
    public void VoterKeyFallbackForUntaggedClient() {
        var s = StartVote();
        Inject("alice", "#1", userId: null);     // login fallback
        Inject("alice", "#1", userId: null);     // same login → still one vote
        Assert.Equal(1, s.Tallies[1]);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: 11 new tests fail (no parser yet); the original 10 still pass.

- [ ] **Step 3: Add parsing + tally to `VoteSession.cs`**

Replace the body of `VoteSession.cs` with:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

public sealed class VoteSession : IDisposable {
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;
    private readonly VoteParsingPolicy _parsing;
    private readonly VoteReceiptPolicy _receipts;
    private readonly Func<VoteSnapshot, ReceiptKind, string>? _formatReceipt;

    private readonly DateTimeOffset _openedAt;
    private readonly Dictionary<int, int> _tallies;
    private readonly Dictionary<string, int> _votersByKey = new();   // VoterKey -> last option chosen
    private readonly Regex _voteRegex;
    private VoteSessionState _state = VoteSessionState.Open;

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public VoteSessionState State => _state;
    public int? WinnerIndex { get; private set; }
    public TimeSpan TimeRemaining => MaxZero(_openedAt + Duration - _clock.UtcNow);
    public IReadOnlyDictionary<int, int> Tallies => new Dictionary<int, int>(_tallies);

    public event EventHandler<VoteSession>? TallyChanged;

    internal VoteSession(
        string id,
        string label,
        IReadOnlyList<VoteOption> options,
        TimeSpan duration,
        IChatService chat,
        IClock clock,
        ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher,
        Random random,
        VoteParsingPolicy parsingPolicy,
        VoteReceiptPolicy receiptPolicy,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt) {

        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        if (options is null || options.Count == 0) throw new ArgumentException("at least one option required", nameof(options));
        if (options.Count > 10) throw new ArgumentException("max 10 options (0..9)", nameof(options));
        for (int i = 0; i < options.Count; i++) {
            if (string.IsNullOrWhiteSpace(options[i].Label))
                throw new ArgumentException($"option {i} has empty label", nameof(options));
            if (options[i].Index != i)
                throw new ArgumentException($"option {i} has wrong Index ({options[i].Index})", nameof(options));
        }
        if (duration < TimeSpan.FromSeconds(1)) throw new ArgumentException("duration must be >= 1s", nameof(duration));

        Id = id; Label = label; Options = options; Duration = duration;
        _chat = chat; _clock = clock; _scheduler = scheduler; _dispatcher = dispatcher;
        _random = random; _parsing = parsingPolicy; _receipts = receiptPolicy;
        _formatReceipt = formatReceipt;

        _openedAt = clock.UtcNow;
        _tallies = options.ToDictionary(o => o.Index, _ => 0);
        _voteRegex = BuildRegex(parsingPolicy);

        _chat.MessageReceived += OnChatMessage;
    }

    private static Regex BuildRegex(VoteParsingPolicy p) {
        var prefix = (p.AcceptHashCommands, p.AcceptBangCommands) switch {
            (true, true) => "[#!]?",
            (true, false) => "#?",
            (false, true) => "!?",
            _ => ""
        };
        return new Regex($@"^{prefix}(\d+)(?:\s|$)", RegexOptions.Compiled);
    }

    private void OnChatMessage(object? sender, ChatMessage msg) {
        if (_state != VoteSessionState.Open) return;
        var match = _voteRegex.Match(msg.Text);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var idx)) return;
        if (idx < 0 || idx >= Options.Count) return;

        var key = msg.VoterKey;
        if (_votersByKey.TryGetValue(key, out var prior)) {
            if (prior == idx) return;          // same vote: no-op, no event
            _tallies[prior]--;
        }
        _votersByKey[key] = idx;
        _tallies[idx]++;
        TallyChanged?.Invoke(this, this);
    }

    public VoteSnapshot Snapshot() => new(
        Id: Id, Label: Label, Options: Options,
        Duration: Duration, TimeRemaining: TimeRemaining,
        Tallies: new Dictionary<int, int>(_tallies),
        State: _state, WinnerIndex: WinnerIndex,
        RandomTieAmong: null, NoVotesReceived: false,
        DisconnectGap: TimeSpan.Zero);

    public void Dispose() {
        if (_state == VoteSessionState.Disposed) return;
        _chat.MessageReceived -= OnChatMessage;
        _state = VoteSessionState.Disposed;
    }

    private static TimeSpan MaxZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 21, Failed: 0`. (10 from 5.1 + 11 new.)

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.2: VoteSession parsing + tally (latest-wins, dedup, out-of-range)

11 new tests covering: hash/bare/bang vote acceptance, bang policy
toggle, latest-vote-wins replacement, out-of-range/non-anchored/
ordinals ignored, TallyChanged fires only on actual change, multi-user
accumulation, untagged-client login fallback.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.3: Tie-break + no-voter random pick (TDD)

Add `ComputeWinner()` (called by Task 5.4's CloseNow). Uniform random across tied options; uniform random across all options when zero votes. Snapshot now reports `RandomTieAmong` and `NoVotesReceived` correctly. (Spec §Closing edge cases; Decisions #6, #7.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void ComputeWinner_SingleMaxReturnsThatIndex() {
        var s = StartVote();
        Inject("alice", "#1"); Inject("bob", "#1"); Inject("carol", "#0");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.Equal(1, winner);
        Assert.Null(tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_TwoWayTie_PicksOneOfTwo_ReportsTie() {
        var s = StartVote();
        Inject("alice", "#0"); Inject("bob", "#1");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1);
        Assert.Equal(2, tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_ThreeWayTie_PicksOneOfThree_ReportsTie() {
        var s = StartVote();
        Inject("alice", "#0"); Inject("bob", "#1"); Inject("carol", "#2");
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1 or 2);
        Assert.Equal(3, tieAmong);
        Assert.False(noVotes);
    }

    [Fact]
    public void ComputeWinner_NoVotes_PicksOneOfAllOptions_ReportsNoVotes() {
        var s = StartVote();
        var (winner, tieAmong, noVotes) = s.ComputeWinnerForTest();
        Assert.True(winner is 0 or 1 or 2);
        Assert.Null(tieAmong);
        Assert.True(noVotes);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: build error (`ComputeWinnerForTest` does not exist).

- [ ] **Step 3: Add winner-computation to `VoteSession.cs`**

Add inside `VoteSession` (above `Dispose`):
```csharp
    /// <summary>(Index, RandomTieAmong, NoVotesReceived). Test-only entry point; Task 5.4 wires this into CloseNow.</summary>
    internal (int Winner, int? TieAmong, bool NoVotes) ComputeWinnerForTest() => ComputeWinner();

    private (int Winner, int? TieAmong, bool NoVotes) ComputeWinner() {
        var voted = _tallies.Where(kv => kv.Value > 0).ToList();
        if (voted.Count == 0) {
            // No-voter random across all options.
            var idx = _random.Next(Options.Count);
            return (idx, null, true);
        }
        var maxCount = voted.Max(kv => kv.Value);
        var tied = voted.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
        if (tied.Count == 1)
            return (tied[0], null, false);
        var pick = tied[_random.Next(tied.Count)];
        return (pick, tied.Count, false);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 25, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.3: VoteSession winner computation — tie-break + no-voter random

ComputeWinner returns (winnerIdx, tieAmong, noVotes). Uniform random
across tied options; uniform random across all options when zero votes.
Tests assert correct (winner, tieAmong, noVotes) tuples for single-max
/ 2-way tie / 3-way tie / no-votes scenarios.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.4: State machine — `CloseNow()`, `Cancel()`, `Dispose()` semantics (TDD)

`CloseNow` triggers winner computation + state→Closed + fires `Closed` event. `Cancel` triggers state→Cancelled (no winner) + fires `Cancelled` event. `Dispose` of an Open session calls `Cancel`. Votes after close are ignored. (Spec §State machine; Decisions #7.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void CloseNow_SetsStateClosed_WinnerIndex_FiresClosedEvent() {
        var s = StartVote();
        Inject("alice", "#2");
        VoteSession? closedSeen = null;
        s.Closed += (_, sess) => closedSeen = sess;

        var winner = s.CloseNow();
        Assert.Equal(2, winner);
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(2, s.WinnerIndex);
        Assert.Same(s, closedSeen);
    }

    [Fact]
    public void Cancel_SetsStateCancelled_NoWinner_FiresCancelledEvent() {
        var s = StartVote();
        Inject("alice", "#1");
        VoteSession? cancelSeen = null;
        s.Cancelled += (_, sess) => cancelSeen = sess;

        s.Cancel();
        Assert.Equal(VoteSessionState.Cancelled, s.State);
        Assert.Null(s.WinnerIndex);
        Assert.Same(s, cancelSeen);
    }

    [Fact]
    public void DisposeOfOpenSessionCancels() {
        var s = StartVote();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Dispose();
        Assert.Equal(VoteSessionState.Disposed, s.State);
        Assert.True(cancelFired);
    }

    [Fact]
    public void DisposeOfClosedSessionIsNoop() {
        var s = StartVote();
        s.CloseNow();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Dispose();
        Assert.Equal(VoteSessionState.Disposed, s.State);
        Assert.False(cancelFired);
    }

    [Fact]
    public void DoubleDisposeIsNoop() {
        var s = StartVote();
        s.Dispose();
        s.Dispose();   // doesn't throw
        Assert.Equal(VoteSessionState.Disposed, s.State);
    }

    [Fact]
    public void VotesAfterCloseAreIgnored() {
        var s = StartVote();
        Inject("alice", "#1");
        s.CloseNow();
        Inject("bob", "#1");          // post-close
        Assert.Equal(1, s.Tallies[1]);   // unchanged from pre-close
    }

    [Fact]
    public void DurationElapsesTriggersClose() {
        var s = StartVote(duration: TimeSpan.FromSeconds(10));
        Inject("alice", "#0");
        var closed = false;
        s.Closed += (_, _) => closed = true;

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.True(closed);
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(0, s.WinnerIndex);
    }

    [Fact]
    public void CloseNowTwiceReturnsSameWinnerWithoutRefiring() {
        var s = StartVote();
        Inject("alice", "#1");
        var closedFires = 0;
        s.Closed += (_, _) => closedFires++;
        var w1 = s.CloseNow();
        var w2 = s.CloseNow();
        Assert.Equal(w1, w2);
        Assert.Equal(1, closedFires);
    }

    [Fact]
    public void CancelOfClosedSessionIsNoop() {
        var s = StartVote();
        s.CloseNow();
        var cancelFired = false;
        s.Cancelled += (_, _) => cancelFired = true;
        s.Cancel();
        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.False(cancelFired);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: build errors about missing `CloseNow`, `Cancel`, `Closed` event, `Cancelled` event.

- [ ] **Step 3: Update `VoteSession.cs`**

Replace `VoteSession.cs` (full file — easier than cherry-picking edits):
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

public sealed class VoteSession : IDisposable {
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;
    private readonly VoteParsingPolicy _parsing;
    private readonly VoteReceiptPolicy _receipts;
    private readonly Func<VoteSnapshot, ReceiptKind, string>? _formatReceipt;

    private readonly DateTimeOffset _openedAt;
    private readonly Dictionary<int, int> _tallies;
    private readonly Dictionary<string, int> _votersByKey = new();
    private readonly Regex _voteRegex;
    private readonly IDisposable _closeTimer;
    private VoteSessionState _state = VoteSessionState.Open;
    private int? _tieAmong;
    private bool _noVotesReceived;

    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public VoteSessionState State => _state;
    public int? WinnerIndex { get; private set; }
    public TimeSpan TimeRemaining => MaxZero(_openedAt + Duration - _clock.UtcNow);
    public IReadOnlyDictionary<int, int> Tallies => new Dictionary<int, int>(_tallies);

    public event EventHandler<VoteSession>? TallyChanged;
    public event EventHandler<VoteSession>? Closed;
    public event EventHandler<VoteSession>? Cancelled;

    internal VoteSession(
        string id, string label, IReadOnlyList<VoteOption> options, TimeSpan duration,
        IChatService chat, IClock clock, ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher, Random random,
        VoteParsingPolicy parsingPolicy, VoteReceiptPolicy receiptPolicy,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt) {

        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required", nameof(label));
        if (options is null || options.Count == 0) throw new ArgumentException("at least one option required", nameof(options));
        if (options.Count > 10) throw new ArgumentException("max 10 options (0..9)", nameof(options));
        for (int i = 0; i < options.Count; i++) {
            if (string.IsNullOrWhiteSpace(options[i].Label))
                throw new ArgumentException($"option {i} has empty label", nameof(options));
            if (options[i].Index != i)
                throw new ArgumentException($"option {i} has wrong Index ({options[i].Index})", nameof(options));
        }
        if (duration < TimeSpan.FromSeconds(1)) throw new ArgumentException("duration must be >= 1s", nameof(duration));

        Id = id; Label = label; Options = options; Duration = duration;
        _chat = chat; _clock = clock; _scheduler = scheduler; _dispatcher = dispatcher;
        _random = random; _parsing = parsingPolicy; _receipts = receiptPolicy;
        _formatReceipt = formatReceipt;

        _openedAt = clock.UtcNow;
        _tallies = options.ToDictionary(o => o.Index, _ => 0);
        _voteRegex = BuildRegex(parsingPolicy);

        _chat.MessageReceived += OnChatMessage;
        _closeTimer = scheduler.Schedule(duration, () => _dispatcher.Post(() => CloseNowInternal(byTimer: true)));
    }

    private static Regex BuildRegex(VoteParsingPolicy p) {
        var prefix = (p.AcceptHashCommands, p.AcceptBangCommands) switch {
            (true, true) => "[#!]?",
            (true, false) => "#?",
            (false, true) => "!?",
            _ => ""
        };
        return new Regex($@"^{prefix}(\d+)(?:\s|$)", RegexOptions.Compiled);
    }

    private void OnChatMessage(object? sender, ChatMessage msg) {
        if (_state != VoteSessionState.Open) return;
        var match = _voteRegex.Match(msg.Text);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var idx)) return;
        if (idx < 0 || idx >= Options.Count) return;

        var key = msg.VoterKey;
        if (_votersByKey.TryGetValue(key, out var prior)) {
            if (prior == idx) return;
            _tallies[prior]--;
        }
        _votersByKey[key] = idx;
        _tallies[idx]++;
        TallyChanged?.Invoke(this, this);
    }

    public int CloseNow() {
        if (_state is VoteSessionState.Closed or VoteSessionState.Cancelled or VoteSessionState.Disposed)
            return WinnerIndex ?? 0;
        return CloseNowInternal(byTimer: false);
    }

    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _state = VoteSessionState.Closed;
        Closed?.Invoke(this, this);
        return winner;
    }

    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _state = VoteSessionState.Cancelled;
        Cancelled?.Invoke(this, this);
    }

    internal (int Winner, int? TieAmong, bool NoVotes) ComputeWinnerForTest() => ComputeWinner();

    private (int Winner, int? TieAmong, bool NoVotes) ComputeWinner() {
        var voted = _tallies.Where(kv => kv.Value > 0).ToList();
        if (voted.Count == 0) {
            var idx = _random.Next(Options.Count);
            return (idx, null, true);
        }
        var maxCount = voted.Max(kv => kv.Value);
        var tied = voted.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
        if (tied.Count == 1) return (tied[0], null, false);
        return (tied[_random.Next(tied.Count)], tied.Count, false);
    }

    public VoteSnapshot Snapshot() => new(
        Id: Id, Label: Label, Options: Options,
        Duration: Duration, TimeRemaining: TimeRemaining,
        Tallies: new Dictionary<int, int>(_tallies),
        State: _state, WinnerIndex: WinnerIndex,
        RandomTieAmong: _tieAmong, NoVotesReceived: _noVotesReceived,
        DisconnectGap: TimeSpan.Zero);

    public void Dispose() {
        if (_state == VoteSessionState.Disposed) return;
        if (_state == VoteSessionState.Open) Cancel();
        _state = VoteSessionState.Disposed;
    }

    private static TimeSpan MaxZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 34, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.4: VoteSession state machine — CloseNow / Cancel / Dispose

9 new tests covering: CloseNow sets state Closed + WinnerIndex + fires
Closed; Cancel sets state Cancelled + no winner + fires Cancelled;
Dispose of open session cancels; Dispose of closed is a no-op; double
Dispose is a no-op; votes after close ignored; scheduler-driven close
when duration elapses; idempotent CloseNow; Cancel-of-Closed is no-op.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.5: Periodic tally timer + adaptive cadence + receipt wiring (TDD)

Open receipt at start; periodic tally at adaptive cadence (`max(5s, duration/5)` when policy is null); close receipt at end. Receipts sent via `IChatService.SendMessageAsync` at the appropriate priority. (Spec §Receipts; Optional Enhancement #1.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void OpenReceiptIsSentAtStartWithNormalPriority() {
        var s = StartVote(label: "card reward");
        Assert.Single(Chat.SentMessages);
        Assert.Equal(OutgoingMessagePriority.Normal, Chat.SentMessages[0].Priority);
        Assert.Contains("card reward", Chat.SentMessages[0].Text);
    }

    [Fact]
    public void PeriodicTally_AdaptiveCadence_30sVote_Fires_ApproxEvery6s() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));   // adaptive: max(5, 30/5) = 6
        Inject("alice", "#0");
        // Initial open receipt is at index 0.
        Assert.Single(Chat.SentMessages);

        Scheduler.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(2, Chat.SentMessages.Count);
        Assert.Equal(OutgoingMessagePriority.Low, Chat.SentMessages[1].Priority);
        Assert.Contains("0=1", Chat.SentMessages[1].Text);

        Scheduler.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(3, Chat.SentMessages.Count);
    }

    [Fact]
    public void PeriodicTally_FixedCadence_HonoursPolicy() {
        var s = StartVote(
            duration: TimeSpan.FromSeconds(60),
            receipts: VoteReceiptPolicy.WithFixedCadence(TimeSpan.FromSeconds(10)));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, Chat.SentMessages.Count);   // open + 1 periodic
    }

    [Fact]
    public void PeriodicTally_IsSkippedWhenAllZero() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));   // adaptive 6s
        // No votes injected.
        Scheduler.Advance(TimeSpan.FromSeconds(6));
        Assert.Single(Chat.SentMessages);  // still just the open receipt — no periodic
    }

    [Fact]
    public void PeriodicTally_IsSkippedWhenIdenticalToPrevious() {
        var s = StartVote(duration: TimeSpan.FromSeconds(60));   // adaptive 12s
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(12));   // sends periodic #1 (0=1)
        var afterFirst = Chat.SentMessages.Count;

        Scheduler.Advance(TimeSpan.FromSeconds(12));   // identical tally → skip
        Assert.Equal(afterFirst, Chat.SentMessages.Count);

        Inject("bob", "#1");
        Scheduler.Advance(TimeSpan.FromSeconds(12));   // tally now different → send
        Assert.Equal(afterFirst + 1, Chat.SentMessages.Count);
    }

    [Fact]
    public void PeriodicTally_Disabled_WhenZeroCadence() {
        var s = StartVote(receipts: VoteReceiptPolicy.Silent);
        Inject("alice", "#0");
        Scheduler.Advance(TimeSpan.FromSeconds(60));
        // Silent policy: no open, no periodic, no close.
        Assert.Empty(Chat.SentMessages);
    }

    [Fact]
    public void CloseReceiptIsSentAtCloseWithHighPriority() {
        var s = StartVote();
        Inject("alice", "#1");
        s.CloseNow();
        var lastSend = Chat.SentMessages[^1];
        Assert.Equal(OutgoingMessagePriority.High, lastSend.Priority);
        Assert.Contains("Defend", lastSend.Text);
    }

    [Fact]
    public void Cancel_DoesNotSendCloseReceipt() {
        var s = StartVote();
        var openCount = Chat.SentMessages.Count;
        s.Cancel();
        Assert.Equal(openCount, Chat.SentMessages.Count);
    }
```

- [ ] **Step 2: Run to verify failures**

Expected: 8 new tests fail (no receipt wiring).

- [ ] **Step 3: Update `VoteSession.cs`**

Add three private helpers and wire them to start/timer/close. Replace constructor body's tail and add at-end-of-file:

After `_chat.MessageReceived += OnChatMessage;` (in the constructor), add:
```csharp
        // Periodic tally cadence (adaptive default)
        var cadence = ResolveCadence(_receipts.PeriodicTallyEvery, duration);
        if (cadence > TimeSpan.Zero)
            _periodicTimer = scheduler.SchedulePeriodic(cadence, () => _dispatcher.Post(SendPeriodicReceipt));

        // Open receipt
        if (_receipts.AnnounceOnOpen) {
            _ = SendReceipt(ReceiptKind.Open, OutgoingMessagePriority.Normal);
        }
```

Add the field:
```csharp
    private readonly IDisposable? _periodicTimer;
    private string? _lastPeriodicSent;
```

Add helper methods (above `private static TimeSpan MaxZero`):
```csharp
    private static TimeSpan ResolveCadence(TimeSpan? configured, TimeSpan duration) {
        if (configured is null) {
            // adaptive: max(5s, duration/5)
            var adaptive = TimeSpan.FromSeconds(Math.Max(5.0, duration.TotalSeconds / 5.0));
            return adaptive;
        }
        return configured.Value;   // TimeSpan.Zero disables
    }

    private void SendPeriodicReceipt() {
        if (_state != VoteSessionState.Open) return;
        if (_tallies.Values.All(c => c == 0)) return;            // skip when all zero
        var text = FormatReceipt(ReceiptKind.PeriodicTally);
        if (text == _lastPeriodicSent) return;                   // skip identical
        _lastPeriodicSent = text;
        _ = _chat.SendMessageAsync(text, OutgoingMessagePriority.Low);
    }

    private System.Threading.Tasks.Task SendReceipt(ReceiptKind kind, OutgoingMessagePriority priority) {
        var text = FormatReceipt(kind);
        return _chat.SendMessageAsync(text, priority);
    }

    private string FormatReceipt(ReceiptKind kind) {
        if (_formatReceipt is not null) return _formatReceipt(Snapshot(), kind);
        return kind switch {
            ReceiptKind.Open => EnglishReceipts.FormatOpen(Snapshot()),
            ReceiptKind.PeriodicTally => EnglishReceipts.FormatPeriodicTally(Snapshot()),
            ReceiptKind.Close => EnglishReceipts.FormatClose(Snapshot()),
            _ => string.Empty,
        };
    }
```

Update `CloseNowInternal` to send the close receipt and dispose the periodic timer:
```csharp
    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        if (_receipts.AnnounceOnClose) {
            _ = SendReceipt(ReceiptKind.Close, OutgoingMessagePriority.High);
        }
        Closed?.Invoke(this, this);
        return winner;
    }
```

Update `Cancel`:
```csharp
    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Cancelled;
        Cancelled?.Invoke(this, this);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 42, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.5: VoteSession periodic tally + receipt wiring + adaptive cadence

8 new tests covering: open receipt at Normal priority; adaptive cadence
max(5s, duration/5); fixed cadence honoured; periodic skipped when all
tallies zero; periodic skipped when identical to previous; Silent policy
suppresses everything; close receipt at High priority; Cancel skips
close receipt. Adaptive cadence is Optional Enhancement #1.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.6: `AwaitWinnerAsync` + no-await Warn (TDD)

`AwaitWinnerAsync` returns a `Task<int>` that completes with the winner when the session closes (or `OperationCanceledException` if cancelled or the caller's `CancellationToken` fires). Tracks whether anyone awaited; logs Warn on close if not. Uses `RunContinuationsAsynchronously` to avoid main-thread reentrancy. (Spec §VoteSession; Optional Enhancement #12.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public async Task AwaitWinnerAsync_CompletesWithWinner_WhenClosed() {
        var s = StartVote();
        Inject("alice", "#2");
        var task = s.AwaitWinnerAsync();
        s.CloseNow();
        var winner = await task;
        Assert.Equal(2, winner);
    }

    [Fact]
    public async Task AwaitWinnerAsync_CancelsWhenSessionCancelled() {
        var s = StartVote();
        var task = s.AwaitWinnerAsync();
        s.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task AwaitWinnerAsync_CallerCancellation_OnlyCancelsThatAwaiter_NotSession() {
        var s = StartVote();
        using var cts = new System.Threading.CancellationTokenSource();
        var task = s.AwaitWinnerAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.Equal(VoteSessionState.Open, s.State);   // session still open

        // Another awaiter still works:
        var task2 = s.AwaitWinnerAsync();
        Inject("alice", "#1");
        s.CloseNow();
        Assert.Equal(1, await task2);
    }

    [Fact]
    public void Closed_WithoutAwait_LogsWarn() {
        var captured = new List<(LogLevel Level, string Msg)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (lvl, msg, _) => captured.Add((lvl, msg));
        try {
            var s = StartVote();
            Inject("alice", "#0");
            s.CloseNow();
            Assert.Contains(captured, e => e.Level == LogLevel.Warn && e.Msg.Contains("AwaitWinnerAsync was never called"));
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public void Closed_WithAwait_DoesNotLogNoAwaitWarn() {
        var captured = new List<(LogLevel Level, string Msg)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (lvl, msg, _) => captured.Add((lvl, msg));
        try {
            var s = StartVote();
            _ = s.AwaitWinnerAsync();   // call once — that's enough
            Inject("alice", "#0");
            s.CloseNow();
            Assert.DoesNotContain(captured, e => e.Level == LogLevel.Warn && e.Msg.Contains("AwaitWinnerAsync"));
        } finally {
            TiLog.Sink = prior;
        }
    }
```

(The first test's `await task` after `CloseNow()` works because the TCS uses `RunContinuationsAsynchronously` and the test runs on the calling thread; xUnit awaits the returned `Task<int>` which is already completed.)

- [ ] **Step 2: Run to verify failures**

Expected: build error about missing `AwaitWinnerAsync`.

- [ ] **Step 3: Add to `VoteSession.cs`**

Add fields:
```csharp
    private readonly System.Threading.Tasks.TaskCompletionSource<int> _winnerTcs =
        new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _anyoneAwaited;
```

Add method:
```csharp
    public async System.Threading.Tasks.Task<int> AwaitWinnerAsync(System.Threading.CancellationToken ct = default) {
        _anyoneAwaited = true;
        using var reg = ct.Register(() => { /* token-cancellation cancels only this awaiter, not the session */ });
        var winnerTask = _winnerTcs.Task;
        var canceledTask = System.Threading.Tasks.Task.Delay(System.Threading.Timeout.Infinite, ct);
        var done = await System.Threading.Tasks.Task.WhenAny(winnerTask, canceledTask).ConfigureAwait(false);
        if (done == canceledTask) ct.ThrowIfCancellationRequested();
        return await winnerTask.ConfigureAwait(false);
    }
```

Update `CloseNowInternal` to complete the TCS and check the no-await flag:
```csharp
    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        if (_receipts.AnnounceOnClose) {
            _ = SendReceipt(ReceiptKind.Close, OutgoingMessagePriority.High);
        }
        _winnerTcs.TrySetResult(winner);
        if (!_anyoneAwaited)
            TiLog.Warn($"VoteSession {Id} closed with winner {winner} but AwaitWinnerAsync was never called — caller likely forgot to consume the result.");
        Closed?.Invoke(this, this);
        return winner;
    }
```

Update `Cancel` to cancel the TCS:
```csharp
    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Cancelled;
        _winnerTcs.TrySetCanceled();
        Cancelled?.Invoke(this, this);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 47, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.6: AwaitWinnerAsync + no-await Warn

5 new tests covering: completes with winner on close; cancels on
session.Cancel(); caller-supplied CancellationToken cancels only that
awaiter not the session; Warn logged on close if no one awaited; no
Warn when at least one caller awaited. RunContinuationsAsynchronously
on the underlying TCS prevents main-thread reentrancy from Harmony
patches that block-await. Optional Enhancement #12.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.7: Disconnect-gap tracking (TDD)

Subscribe to `IChatService.ConnectionStateChanged`. Accumulate total offline time during the session and report it via `Snapshot.DisconnectGap`. Close receipt mentions the gap. (Spec §Disconnect tolerance.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void DisconnectGap_Zero_WhenChatStaysConnected() {
        var s = StartVote();
        Inject("alice", "#0");
        s.CloseNow();
        Assert.Equal(TimeSpan.Zero, s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void DisconnectGap_AccumulatesOfflineTime_DuringMidVoteOutage() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.Reconnecting);   // offline starts here
        Scheduler.Advance(TimeSpan.FromSeconds(8));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);   // back online
        Scheduler.Advance(TimeSpan.FromSeconds(5));
        s.CloseNow();
        Assert.Equal(TimeSpan.FromSeconds(8), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void DisconnectGap_AccumulatesAcrossMultipleOutages() {
        var s = StartVote(duration: TimeSpan.FromSeconds(60));
        Inject("alice", "#0");

        Scheduler.Advance(TimeSpan.FromSeconds(5));
        Chat.SimulateState(ChatConnectionState.Reconnecting);
        Scheduler.Advance(TimeSpan.FromSeconds(3));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);

        Scheduler.Advance(TimeSpan.FromSeconds(10));
        Chat.SimulateState(ChatConnectionState.Disconnected);
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);

        s.CloseNow();
        Assert.Equal(TimeSpan.FromSeconds(10), s.Snapshot().DisconnectGap);
    }

    [Fact]
    public void CloseReceipt_MentionsOfflineGap_WhenPresent() {
        var s = StartVote();
        Inject("alice", "#0");
        Chat.SimulateState(ChatConnectionState.Reconnecting);
        Scheduler.Advance(TimeSpan.FromSeconds(8));
        Chat.SimulateState(ChatConnectionState.ConnectedReadWrite);
        s.CloseNow();
        var closeReceipt = Chat.SentMessages[^1].Text;
        Assert.Contains("offline", closeReceipt);
        Assert.Contains("8s", closeReceipt);
    }
```

- [ ] **Step 2: Run to verify failures**

Expected: 4 new tests fail.

- [ ] **Step 3: Add disconnect-tracking to `VoteSession.cs`**

Add fields:
```csharp
    private TimeSpan _disconnectGapAccum;
    private DateTimeOffset? _disconnectStartedAt;
```

In the constructor (after `_chat.MessageReceived += OnChatMessage;`):
```csharp
        _chat.ConnectionStateChanged += OnChatConnectionStateChanged;
        if (!IsChatOnline(_chat.State)) _disconnectStartedAt = clock.UtcNow;
```

Add helpers:
```csharp
    private static bool IsChatOnline(ChatConnectionState state) =>
        state == ChatConnectionState.ConnectedReadOnly ||
        state == ChatConnectionState.ConnectedReadWrite;

    private void OnChatConnectionStateChanged(object? sender, ChatConnectionChangedEventArgs e) {
        if (_state != VoteSessionState.Open) return;
        var nowOnline = IsChatOnline(e.NewState);
        var wasOnline = IsChatOnline(e.OldState);
        if (wasOnline && !nowOnline) {
            _disconnectStartedAt = _clock.UtcNow;
        } else if (!wasOnline && nowOnline && _disconnectStartedAt is { } start) {
            _disconnectGapAccum += _clock.UtcNow - start;
            _disconnectStartedAt = null;
        }
    }
```

Update `Snapshot()` to use `_disconnectGapAccum` plus any in-progress gap:
```csharp
    public VoteSnapshot Snapshot() {
        var liveGap = _disconnectGapAccum;
        if (_disconnectStartedAt is { } s && _state == VoteSessionState.Open)
            liveGap += _clock.UtcNow - s;
        return new VoteSnapshot(
            Id: Id, Label: Label, Options: Options,
            Duration: Duration, TimeRemaining: TimeRemaining,
            Tallies: new Dictionary<int, int>(_tallies),
            State: _state, WinnerIndex: WinnerIndex,
            RandomTieAmong: _tieAmong, NoVotesReceived: _noVotesReceived,
            DisconnectGap: liveGap);
    }
```

Update `CloseNowInternal` to finalise gap before computing snapshot:
```csharp
    private int CloseNowInternal(bool byTimer) {
        if (_state != VoteSessionState.Open) return WinnerIndex ?? 0;
        // Finalise any in-progress disconnect gap
        if (_disconnectStartedAt is { } start) {
            _disconnectGapAccum += _clock.UtcNow - start;
            _disconnectStartedAt = null;
        }
        _state = VoteSessionState.Closing;
        var (winner, tieAmong, noVotes) = ComputeWinner();
        WinnerIndex = winner;
        _tieAmong = tieAmong;
        _noVotesReceived = noVotes;
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionStateChanged -= OnChatConnectionStateChanged;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        if (_receipts.AnnounceOnClose) {
            _ = SendReceipt(ReceiptKind.Close, OutgoingMessagePriority.High);
        }
        _winnerTcs.TrySetResult(winner);
        if (!_anyoneAwaited)
            TiLog.Warn($"VoteSession {Id} closed with winner {winner} but AwaitWinnerAsync was never called — caller likely forgot to consume the result.");
        Closed?.Invoke(this, this);
        return winner;
    }
```

Same unsubscribe in `Cancel`:
```csharp
    public void Cancel() {
        if (_state != VoteSessionState.Open) return;
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionStateChanged -= OnChatConnectionStateChanged;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Cancelled;
        _winnerTcs.TrySetCanceled();
        Cancelled?.Invoke(this, this);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 51, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.7: VoteSession disconnect-gap tracking

VoteSession subscribes to ChatService.ConnectionStateChanged and
accumulates offline time. Snapshot.DisconnectGap reflects total time
chat was offline during the vote (including any in-progress gap when
sampled mid-vote). Close receipt mentions the gap when > zero.
4 new tests.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5.8: Voter dict size cap (TDD)

Cap the per-session voter dict at 10,000. Further unique voters dropped with a single Warn log per session. (Spec §Tally rules.)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `tests/Voting/VoteSessionTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void VoterDict_DropsBeyond10k_LogsWarnOnce() {
        var captured = new List<(LogLevel, string)>();
        var prior = TiLog.Sink;
        TiLog.Sink = (l, m, _) => captured.Add((l, m));
        try {
            var s = StartVote();
            for (int i = 0; i < 10_005; i++)
                Inject($"u{i}", "#0", userId: $"id-{i}");

            Assert.Equal(10_000, s.Tallies[0]);
            var warns = captured.Where(c => c.Item1 == LogLevel.Warn && c.Item2.Contains("voter cap")).ToList();
            Assert.Single(warns);   // only one warn no matter how many overflows
        } finally {
            TiLog.Sink = prior;
        }
    }

    [Fact]
    public void VoterDict_ExistingVoterCanStillChangeVote_WhenAtCap() {
        var s = StartVote();
        for (int i = 0; i < 10_000; i++)
            Inject($"u{i}", "#0", userId: $"id-{i}");

        // u0 changes vote — should still be honoured because they're already in the dict
        Inject("u0", "#1", userId: "id-0");
        Assert.Equal(9_999, s.Tallies[0]);
        Assert.Equal(1, s.Tallies[1]);
    }
```

- [ ] **Step 2: Run to verify failures**

Expected: 2 new tests fail (voter dict has no cap yet).

- [ ] **Step 3: Update `OnChatMessage` in `VoteSession.cs`**

Add a const + flag near other fields:
```csharp
    private const int MaxVoters = 10_000;
    private bool _voterCapWarnLogged;
```

Replace `OnChatMessage` body:
```csharp
    private void OnChatMessage(object? sender, ChatMessage msg) {
        if (_state != VoteSessionState.Open) return;
        var match = _voteRegex.Match(msg.Text);
        if (!match.Success) return;
        if (!int.TryParse(match.Groups[1].Value, out var idx)) return;
        if (idx < 0 || idx >= Options.Count) return;

        var key = msg.VoterKey;
        var existing = _votersByKey.TryGetValue(key, out var prior);
        if (!existing && _votersByKey.Count >= MaxVoters) {
            if (!_voterCapWarnLogged) {
                TiLog.Warn($"VoteSession {Id}: voter cap of {MaxVoters} reached; dropping further unique voters.");
                _voterCapWarnLogged = true;
            }
            return;
        }
        if (existing) {
            if (prior == idx) return;
            _tallies[prior]--;
        }
        _votersByKey[key] = idx;
        _tallies[idx]++;
        TallyChanged?.Invoke(this, this);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteSessionTests"`
Expected: `Passed: 53, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionTests.cs
git commit -m @'
plan-a/5.8: VoteSession voter-dict cap (10k) with single-warn

New unique voters past 10,000 are dropped silently after one Warn log
per session. Existing voters in the dict can still change their votes
even at the cap. 2 new tests; VoteSession suite total: 53 passing.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 6: `VoteCoordinator` + `Voter` facade (2 tasks)

### Task 6.1: `VoteCoordinator` with concurrency invariant (TDD)

Instance-based session owner. `Start(...)` builds the `VoteOption` list with correct indices, constructs a `VoteSession`, throws if a session is already open. (Spec §VoteCoordinator and Voter; Decisions #9.)

**Files:**
- Create: `src/Ti/Voting/VoteCoordinator.cs`
- Test: `tests/Voting/VoteCoordinatorTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Voting/VoteCoordinatorTests.cs`:
```csharp
using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteCoordinatorTests {
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeChatService _chat = new();
    private readonly FakeTimerScheduler _scheduler;
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly Random _rng = new(7);

    public VoteCoordinatorTests() {
        _scheduler = new FakeTimerScheduler(_clock);
        _chat.ConnectAsync("test", new ChatCredentials("bot", "abc")).GetAwaiter().GetResult();
    }

    private VoteCoordinator NewCoord() => new(_chat, _clock, _scheduler, _dispatcher, _rng);

    [Fact]
    public void Start_BuildsOptionListWithCorrectIndices() {
        var c = NewCoord();
        var s = c.Start("card reward", new[] { "Bash", "Defend", "Strike" }, TimeSpan.FromSeconds(30));
        Assert.Equal(0, s.Options[0].Index);
        Assert.Equal("Bash", s.Options[0].Label);
        Assert.Equal(2, s.Options[2].Index);
    }

    [Fact]
    public void Start_AssignsCurrentSession() {
        var c = NewCoord();
        var s = c.Start("test", new[] { "a", "b" }, TimeSpan.FromSeconds(10));
        Assert.Same(s, c.CurrentSession);
    }

    [Fact]
    public void Start_WhileOpen_Throws() {
        var c = NewCoord();
        c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        Assert.Throws<InvalidOperationException>(() =>
            c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Start_AfterPriorClosed_Succeeds() {
        var c = NewCoord();
        var s1 = c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s1.CloseNow();
        var s2 = c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30));
        Assert.Same(s2, c.CurrentSession);
    }

    [Fact]
    public void Start_AfterPriorCancelled_Succeeds() {
        var c = NewCoord();
        var s1 = c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s1.Cancel();
        var s2 = c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30));
        Assert.Same(s2, c.CurrentSession);
    }

    [Fact]
    public void CurrentSession_ClearedAfterClose() {
        var c = NewCoord();
        var s = c.Start("v", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s.CloseNow();
        Assert.Null(c.CurrentSession);
    }

    [Fact]
    public void Dispose_CancelsActiveSession() {
        var c = NewCoord();
        var s = c.Start("v", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        c.Dispose();
        Assert.Equal(VoteSessionState.Cancelled, s.State);
    }
}
```

- [ ] **Step 2: Run to verify failures**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteCoordinatorTests"`
Expected: build error (no `VoteCoordinator`).

- [ ] **Step 3: Create `src/Ti/Voting/VoteCoordinator.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Owner of the per-streamer-channel vote lifecycle. One instance per
/// IChatService. Holds the currently-active VoteSession and enforces the
/// "strictly one open vote per coordinator" invariant.
/// </summary>
public sealed class VoteCoordinator : IDisposable {
    private readonly IChatService _chat;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly Random _random;

    public IChatService Chat => _chat;
    public VoteSession? CurrentSession { get; private set; }

    public VoteCoordinator(
        IChatService chat,
        IClock clock,
        ITimerScheduler scheduler,
        IMainThreadDispatcher dispatcher,
        Random? random = null) {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _random = random ?? new Random();
    }

    public VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt = null,
        CancellationToken ct = default) {

        if (CurrentSession is { State: VoteSessionState.Open })
            throw new InvalidOperationException(
                $"VoteCoordinator already has an open session ({CurrentSession.Id}); dispose/close it first.");

        var optionList = new List<VoteOption>(options.Count);
        for (int i = 0; i < options.Count; i++)
            optionList.Add(new VoteOption(i, options[i]));

        var id = $"{Slug(label)}-{_clock.UtcNow:yyyyMMddTHHmmssfff}";
        var session = new VoteSession(
            id: id, label: label, options: optionList, duration: duration,
            chat: _chat, clock: _clock, scheduler: _scheduler,
            dispatcher: _dispatcher, random: _random,
            parsingPolicy: parsing ?? VoteParsingPolicy.Default,
            receiptPolicy: receipts ?? VoteReceiptPolicy.Default,
            formatReceipt: formatReceipt);

        CurrentSession = session;
        session.Closed += OnSessionEnded;
        session.Cancelled += OnSessionEnded;

        return session;
    }

    private void OnSessionEnded(object? sender, VoteSession s) {
        if (CurrentSession == s) CurrentSession = null;
    }

    private static string Slug(string s) {
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
            chars[i] = char.IsLetterOrDigit(s[i]) ? char.ToLowerInvariant(s[i]) : '-';
        return new string(chars);
    }

    public void Dispose() {
        CurrentSession?.Cancel();
        CurrentSession = null;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteCoordinatorTests"`
Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/VoteCoordinator.cs tests/Voting/VoteCoordinatorTests.cs
git commit -m @'
plan-a/6.1: VoteCoordinator with concurrency invariant

Instance-based session owner. Start() builds the VoteOption list,
constructs a VoteSession, and refuses to overlap with an open session.
CurrentSession clears automatically when the session closes/cancels.
Dispose cancels any active session. 7 tests.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 6.2: `Voter` static facade

Thin convenience for Harmony patches. `Voter.Default` is a `VoteCoordinator?` set by `ModEntry.Initialize` (Plan B). `Voter.Start(...)` delegates to `Default`.

**Files:**
- Create: `src/Ti/Voting/Voter.cs`
- Test: extend `tests/Voting/VoteCoordinatorTests.cs` with a small `VoterTests` class.

- [ ] **Step 1: Append tests**

`tests/Voting/VoterTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoterTests : IDisposable {
    private readonly VoteCoordinator? _prior = Voter.Default;
    public void Dispose() => Voter.Default = _prior;

    [Fact]
    public void StartWithNullDefault_Throws() {
        Voter.Default = null;
        Assert.Throws<InvalidOperationException>(() =>
            Voter.Start("x", new[] { "a", "b" }, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StartWithDefault_DelegatesToCoordinator() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var chat = new FakeChatService();
        chat.ConnectAsync("c", new ChatCredentials("u", "abc")).GetAwaiter().GetResult();
        var coord = new VoteCoordinator(chat, clock, new FakeTimerScheduler(clock), new ImmediateDispatcher(), new Random(0));
        Voter.Default = coord;

        var s = Voter.Start("test", new[] { "a", "b" }, TimeSpan.FromSeconds(10));
        Assert.Same(s, coord.CurrentSession);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Expected: build error (`Voter` does not exist).

- [ ] **Step 3: Create `src/Ti/Voting/Voter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;

namespace SlayTheStreamer2.Ti.Voting;

/// <summary>
/// Process-wide static facade over a VoteCoordinator. Set Voter.Default once
/// from ModEntry.Initialize (Plan B); Harmony-patch call sites then use
/// Voter.Start(...) without plumbing a coordinator reference everywhere.
///
/// For multiplayer / multi-channel scenarios, construct VoteCoordinator
/// instances directly instead of using this facade.
/// </summary>
public static class Voter {
    public static VoteCoordinator? Default { get; set; }

    public static VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        Func<VoteSnapshot, ReceiptKind, string>? formatReceipt = null,
        CancellationToken ct = default) {
        var coord = Default
            ?? throw new InvalidOperationException(
                "Voter.Default is not initialised. Set it from ModEntry.Initialize.");
        return coord.Start(label, options, duration, receipts, parsing, formatReceipt, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoterTests"`
Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Voting/Voter.cs tests/Voting/VoterTests.cs
git commit -m @'
plan-a/6.2: Voter static facade

Thin static wrapper over VoteCoordinator for Harmony-patch convenience.
Default is set once from ModEntry.Initialize (Plan B); throws if a
caller invokes Start before Default is set.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 7: `TwitchIrcParser` (3 tasks)

Pure-function parser. Input: one IRC line (no CRLF). Output: a typed `IrcEvent` (or `UnknownIrcEvent` for things we ignore but want to count). Used by `TwitchIrcChatService` (Plan B) and exercised heavily by tests using fixture corpora. (Spec §IRC protocol handling matrix.)

### Task 7.1: `IrcEvent` types + parser scaffolding (PING, RECONNECT, unknown) (TDD)

**Files:**
- Create: `src/Ti/Chat/Internal/IrcEvent.cs`
- Create: `src/Ti/Chat/Internal/TwitchIrcParser.cs`
- Test: `tests/Chat/Internal/TwitchIrcParserTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Chat/Internal/TwitchIrcParserTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Chat.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.Internal;

public class TwitchIrcParserTests {
    [Fact]
    public void Parse_Ping_ExtractsToken() {
        var evt = TwitchIrcParser.Parse("PING :tmi.twitch.tv");
        var ping = Assert.IsType<PingEvent>(evt);
        Assert.Equal("tmi.twitch.tv", ping.Token);
    }

    [Fact]
    public void Parse_Reconnect_ReturnsReconnectEvent() {
        var evt = TwitchIrcParser.Parse(":tmi.twitch.tv RECONNECT");
        Assert.IsType<ReconnectEvent>(evt);
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull() {
        Assert.Null(TwitchIrcParser.Parse(""));
        Assert.Null(TwitchIrcParser.Parse("   "));
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsUnknown() {
        var evt = TwitchIrcParser.Parse(":server FOO bar baz");
        var unk = Assert.IsType<UnknownIrcEvent>(evt);
        Assert.Equal(":server FOO bar baz", unk.Raw);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TwitchIrcParserTests"`
Expected: build error.

- [ ] **Step 3: Create `src/Ti/Chat/Internal/IrcEvent.cs`**

```csharp
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>Discriminated union of parsed Twitch IRC events.</summary>
public abstract record IrcEvent;

public sealed record PingEvent(string Token) : IrcEvent;
public sealed record ReconnectEvent : IrcEvent;
public sealed record PrivmsgEvent(ChatMessage Message) : IrcEvent;
public sealed record NoticeEvent(string? Channel, string Text, string? MsgId) : IrcEvent;
public sealed record CapAckEvent(IReadOnlyList<string> Capabilities) : IrcEvent;
public sealed record CapNakEvent(IReadOnlyList<string> Capabilities) : IrcEvent;
public sealed record UserStateEvent(string Channel, string? DisplayName) : IrcEvent;
public sealed record RoomStateEvent(string Channel, IReadOnlyDictionary<string, string> Tags) : IrcEvent;
public sealed record UnknownIrcEvent(string Raw) : IrcEvent;
```

- [ ] **Step 4: Create `src/Ti/Chat/Internal/TwitchIrcParser.cs`** (Task 7.1 minimal — handles PING, RECONNECT, unknown; PRIVMSG comes in 7.2)

```csharp
using System;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// Pure-function parser for Twitch IRC lines. Input lines are pre-trimmed
/// (no CRLF). Output is a typed IrcEvent; UnknownIrcEvent for commands we
/// don't handle in v0.1.
/// </summary>
public static class TwitchIrcParser {
    public static IrcEvent? Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // PING is the only command that doesn't start with a `:` prefix.
        if (line.StartsWith("PING ", StringComparison.Ordinal)) {
            var tokenStart = "PING ".Length;
            var token = line.Substring(tokenStart).TrimStart(':');
            return new PingEvent(token);
        }

        // Everything else has a leading `:prefix`.
        // Format: :prefix COMMAND [params] [:trailing]
        // (We're ignoring the @tags prefix here; PRIVMSG handling adds it in 7.2.)
        var rest = line;
        if (rest.StartsWith(":", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            rest = rest.Substring(space + 1);
        }
        // Now rest starts with COMMAND.
        if (rest.StartsWith("RECONNECT", StringComparison.Ordinal)) {
            return new ReconnectEvent();
        }

        return new UnknownIrcEvent(line);
    }
}
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TwitchIrcParserTests"`
Expected: `Passed: 4, Failed: 0`.

- [ ] **Step 6: Commit**

```powershell
git add src/Ti/Chat/Internal/IrcEvent.cs src/Ti/Chat/Internal/TwitchIrcParser.cs tests/Chat/Internal/TwitchIrcParserTests.cs
git commit -m @'
plan-a/7.1: TwitchIrcParser scaffolding — PING, RECONNECT, Unknown

IrcEvent discriminated-union with PingEvent / ReconnectEvent /
PrivmsgEvent / NoticeEvent / CapAck / CapNak / UserState / RoomState /
UnknownIrcEvent. Parser handles PING and RECONNECT in this task;
PRIVMSG and the rest land in 7.2 / 7.3.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 7.2: PRIVMSG with tag parsing + IRCv3 tag-value unescaping (TDD)

PRIVMSG line format with tags:
```
@badge-info=;badges=;color=#FF0000;display-name=Alice;user-id=12345 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :#1 vote
```

Tag values use IRCv3 escaping: `\:` → `;`, `\s` → space, `\\` → `\`, `\r` → CR, `\n` → LF. The parser converts to a `ChatMessage`.

**Files:**
- Modify: `src/Ti/Chat/Internal/TwitchIrcParser.cs`
- Modify: `tests/Chat/Internal/TwitchIrcParserTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void Parse_Privmsg_ExtractsLoginAndText() {
        var line = ":alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hello world";
        var evt = TwitchIrcParser.Parse(line);
        var msg = Assert.IsType<PrivmsgEvent>(evt).Message;
        Assert.Equal("alice", msg.Login);
        Assert.Equal("hello world", msg.Text);
    }

    [Fact]
    public void Parse_Privmsg_WithTags_ExtractsUserIdAndDisplayName() {
        var line = "@user-id=12345;display-name=Alice :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("12345", msg.UserId);
        Assert.Equal("Alice", msg.DisplayName);
    }

    [Fact]
    public void Parse_Privmsg_WithoutTags_UserIdIsNull_DisplayNameFallsBackToLogin() {
        var line = ":alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Null(msg.UserId);
        Assert.Equal("alice", msg.DisplayName);
    }

    [Fact]
    public void Parse_Privmsg_BadgeFlagsExtracted() {
        var line = "@badges=subscriber/12,moderator/1,vip/1;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.True(msg.IsSubscriber);
        Assert.True(msg.IsModerator);
        Assert.True(msg.IsVip);
    }

    [Fact]
    public void Parse_Privmsg_BadgesAbsent_FlagsFalse() {
        var line = "@user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.False(msg.IsSubscriber);
        Assert.False(msg.IsModerator);
        Assert.False(msg.IsVip);
    }

    [Fact]
    public void Parse_Privmsg_ReceivedAt_FromTmiSentTs_WhenPresent() {
        var line = "@tmi-sent-ts=1715169600000;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1715169600000);
        Assert.Equal(expected, msg.ReceivedAt);
    }

    [Fact]
    public void Parse_TagValue_Unescapes_ColonSpaceBackslashCRLF() {
        // Tag value: "a\:b\sc\\d\re\nf" → "a;b c\d\re\nf" (the \r and \n are literal control chars)
        var line = "@display-name=a\\:b\\sc\\\\d\\re\\nf;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("a;b c\\d\re\nf", msg.DisplayName);
    }
```

- [ ] **Step 2: Run to verify failure**

Expected: 7 new tests fail (no PRIVMSG handling yet).

- [ ] **Step 3: Replace `TwitchIrcParser.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>Pure-function Twitch IRC parser. Input: one CRLF-stripped line.</summary>
public static class TwitchIrcParser {
    public static IrcEvent? Parse(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        if (line.StartsWith("PING ", StringComparison.Ordinal)) {
            var token = line.Substring("PING ".Length).TrimStart(':');
            return new PingEvent(token);
        }

        // Optional @tags prefix
        IReadOnlyDictionary<string, string> tags = EmptyTags;
        var rest = line;
        if (rest.StartsWith("@", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            tags = ParseTags(rest.Substring(1, space - 1));
            rest = rest.Substring(space + 1);
        }

        // :prefix
        string? prefix = null;
        if (rest.StartsWith(":", StringComparison.Ordinal)) {
            var space = rest.IndexOf(' ');
            if (space < 0) return new UnknownIrcEvent(line);
            prefix = rest.Substring(1, space - 1);
            rest = rest.Substring(space + 1);
        }

        // COMMAND [params] [:trailing]
        var (command, paramsAndTrailing) = SplitCommandAndArgs(rest);

        switch (command) {
            case "RECONNECT": return new ReconnectEvent();
            case "PRIVMSG": return ParsePrivmsg(prefix, paramsAndTrailing, tags);
            default: return new UnknownIrcEvent(line);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTags = new Dictionary<string, string>();

    private static (string Command, string Rest) SplitCommandAndArgs(string s) {
        var space = s.IndexOf(' ');
        if (space < 0) return (s, string.Empty);
        return (s.Substring(0, space), s.Substring(space + 1));
    }

    private static IrcEvent? ParsePrivmsg(string? prefix, string paramsAndTrailing, IReadOnlyDictionary<string, string> tags) {
        if (prefix is null) return null;
        // prefix format: "alice!alice@alice.tmi.twitch.tv"
        var bang = prefix.IndexOf('!');
        var login = bang > 0 ? prefix.Substring(0, bang) : prefix;

        // params + trailing format: "#foo :the message body"
        var colon = paramsAndTrailing.IndexOf(" :", StringComparison.Ordinal);
        var text = colon >= 0 ? paramsAndTrailing.Substring(colon + 2) : paramsAndTrailing;

        tags.TryGetValue("user-id", out var userId);
        tags.TryGetValue("display-name", out var displayNameRaw);
        var displayName = string.IsNullOrEmpty(displayNameRaw) ? login : displayNameRaw;

        var (sub, mod, vip) = ParseBadges(tags.GetValueOrDefault("badges", ""));

        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        if (tags.TryGetValue("tmi-sent-ts", out var ts) && long.TryParse(ts, out var ms)) {
            receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return new PrivmsgEvent(new ChatMessage(
            UserId: string.IsNullOrEmpty(userId) ? null : userId,
            Login: login,
            DisplayName: displayName,
            Text: text,
            ReceivedAt: receivedAt,
            IsSubscriber: sub, IsModerator: mod, IsVip: vip));
    }

    private static IReadOnlyDictionary<string, string> ParseTags(string tagSegment) {
        var dict = new Dictionary<string, string>();
        var entries = tagSegment.Split(';');
        foreach (var entry in entries) {
            var eq = entry.IndexOf('=');
            if (eq < 0) { dict[entry] = string.Empty; continue; }
            var key = entry.Substring(0, eq);
            var rawValue = entry.Substring(eq + 1);
            dict[key] = UnescapeTagValue(rawValue);
        }
        return dict;
    }

    private static string UnescapeTagValue(string raw) {
        if (raw.Length == 0 || raw.IndexOf('\\') < 0) return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++) {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length) { sb.Append(c); continue; }
            var n = raw[++i];
            sb.Append(n switch {
                ':' => ';',
                's' => ' ',
                '\\' => '\\',
                'r' => '\r',
                'n' => '\n',
                _ => n,                       // unknown escape — keep literal
            });
        }
        return sb.ToString();
    }

    private static (bool Sub, bool Mod, bool Vip) ParseBadges(string raw) {
        if (string.IsNullOrEmpty(raw)) return (false, false, false);
        bool sub = false, mod = false, vip = false;
        foreach (var entry in raw.Split(',')) {
            var slash = entry.IndexOf('/');
            var name = slash >= 0 ? entry.Substring(0, slash) : entry;
            switch (name) {
                case "subscriber": case "founder": sub = true; break;
                case "moderator": mod = true; break;
                case "vip": vip = true; break;
            }
        }
        return (sub, mod, vip);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TwitchIrcParserTests"`
Expected: `Passed: 11, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/Internal/TwitchIrcParser.cs tests/Chat/Internal/TwitchIrcParserTests.cs
git commit -m @'
plan-a/7.2: TwitchIrcParser PRIVMSG + tag parsing + IRCv3 unescaping

7 new tests: extracts login/text from prefix and trailing; user-id +
display-name from tags; UserId null + DisplayName falls back to login
when untagged; subscriber/moderator/vip flags from badges; ReceivedAt
from tmi-sent-ts when present; IRCv3 \: \s \\ \r \n unescaping in tag
values.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 7.3: NOTICE / CAP ACK / CAP NAK / USERSTATE / ROOMSTATE + edge cases (TDD)

**Files:**
- Modify: `src/Ti/Chat/Internal/TwitchIrcParser.cs`
- Modify: `tests/Chat/Internal/TwitchIrcParserTests.cs`

- [ ] **Step 1: Append tests**

```csharp
    [Fact]
    public void Parse_Notice_AuthFailed_ExtractsTextAndMsgId() {
        var line = "@msg-id=msg_login_unsuccessful :tmi.twitch.tv NOTICE * :Login authentication failed";
        var n = Assert.IsType<NoticeEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("msg_login_unsuccessful", n.MsgId);
        Assert.Equal("Login authentication failed", n.Text);
    }

    [Fact]
    public void Parse_Notice_ChannelTargeted_ExtractsChannel() {
        var line = "@msg-id=msg_banned :tmi.twitch.tv NOTICE #foo :You are permanently banned from this channel";
        var n = Assert.IsType<NoticeEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", n.Channel);
        Assert.Equal("msg_banned", n.MsgId);
    }

    [Fact]
    public void Parse_CapAck_ExtractsCapabilities() {
        var line = ":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands";
        var ack = Assert.IsType<CapAckEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal(2, ack.Capabilities.Count);
        Assert.Contains("twitch.tv/tags", ack.Capabilities);
    }

    [Fact]
    public void Parse_CapNak_ExtractsCapabilities() {
        var line = ":tmi.twitch.tv CAP * NAK :twitch.tv/membership";
        var nak = Assert.IsType<CapNakEvent>(TwitchIrcParser.Parse(line));
        Assert.Single(nak.Capabilities);
        Assert.Equal("twitch.tv/membership", nak.Capabilities[0]);
    }

    [Fact]
    public void Parse_UserState_ExtractsDisplayName() {
        var line = "@display-name=Bot :tmi.twitch.tv USERSTATE #foo";
        var us = Assert.IsType<UserStateEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", us.Channel);
        Assert.Equal("Bot", us.DisplayName);
    }

    [Fact]
    public void Parse_RoomState_ExposesAllTags() {
        var line = "@emote-only=0;subs-only=0;slow=10 :tmi.twitch.tv ROOMSTATE #foo";
        var rs = Assert.IsType<RoomStateEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", rs.Channel);
        Assert.Equal("10", rs.Tags["slow"]);
    }

    [Fact]
    public void Parse_TruncatedLine_ReturnsUnknown() {
        Assert.IsType<UnknownIrcEvent>(TwitchIrcParser.Parse("@incomplete"));
        Assert.IsType<UnknownIrcEvent>(TwitchIrcParser.Parse(":server"));
    }

    [Fact]
    public void Parse_MultiByteUserText_PreservesText() {
        var line = ":alice!a@a PRIVMSG #foo :こんにちは 🎉";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("こんにちは 🎉", msg.Text);
    }
```

- [ ] **Step 2: Run to verify failure**

Expected: 8 new tests fail.

- [ ] **Step 3: Update `TwitchIrcParser.cs`**

Add cases in the switch:
```csharp
            case "NOTICE": return ParseNotice(paramsAndTrailing, tags);
            case "CAP": return ParseCap(paramsAndTrailing);
            case "USERSTATE": return ParseUserState(paramsAndTrailing, tags);
            case "ROOMSTATE": return ParseRoomState(paramsAndTrailing, tags);
```

Add helper methods:
```csharp
    private static IrcEvent? ParseNotice(string paramsAndTrailing, IReadOnlyDictionary<string, string> tags) {
        // "<target> :<text>" — target is `*` (server-targeted) or `#channel`.
        var colon = paramsAndTrailing.IndexOf(" :", StringComparison.Ordinal);
        if (colon < 0) return new UnknownIrcEvent(paramsAndTrailing);
        var target = paramsAndTrailing.Substring(0, colon);
        var text = paramsAndTrailing.Substring(colon + 2);
        var channel = target.StartsWith("#", StringComparison.Ordinal) ? target : null;
        tags.TryGetValue("msg-id", out var msgId);
        return new NoticeEvent(channel, text, msgId);
    }

    private static IrcEvent? ParseCap(string paramsAndTrailing) {
        // Format: "* ACK :cap1 cap2" or "* NAK :cap1"
        var parts = paramsAndTrailing.Split(' ', 3);
        if (parts.Length < 3) return new UnknownIrcEvent(paramsAndTrailing);
        var verb = parts[1];
        var caps = parts[2].TrimStart(':').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return verb switch {
            "ACK" => new CapAckEvent(caps),
            "NAK" => new CapNakEvent(caps),
            _ => new UnknownIrcEvent(paramsAndTrailing),
        };
    }

    private static IrcEvent? ParseUserState(string paramsAndTrailing, IReadOnlyDictionary<string, string> tags) {
        var channel = paramsAndTrailing.Trim();
        tags.TryGetValue("display-name", out var displayName);
        return new UserStateEvent(channel, string.IsNullOrEmpty(displayName) ? null : displayName);
    }

    private static IrcEvent? ParseRoomState(string paramsAndTrailing, IReadOnlyDictionary<string, string> tags) {
        var channel = paramsAndTrailing.Trim();
        return new RoomStateEvent(channel, tags);
    }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~TwitchIrcParserTests"`
Expected: `Passed: 19, Failed: 0`.

- [ ] **Step 5: Commit**

```powershell
git add src/Ti/Chat/Internal/TwitchIrcParser.cs tests/Chat/Internal/TwitchIrcParserTests.cs
git commit -m @'
plan-a/7.3: TwitchIrcParser NOTICE / CAP / USERSTATE / ROOMSTATE + edges

8 new tests: NOTICE auth-failure (server target * with msg-id);
NOTICE channel-targeted; CAP ACK / NAK with capability list; USERSTATE
display-name; ROOMSTATE all-tags exposed; truncated lines return
UnknownIrcEvent (don't throw); multibyte text preserved through PRIVMSG.

Parser is now feature-complete for v0.1; the IRC fixture-generator
tool (Plan C) will harden the test corpus with real captured lines.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 8: `OutgoingMessageQueue` (1 task)

Token-bucket rate limiter + priority ordering (High > Normal > Low) + stale-Low coalescing. (Spec §Outgoing send queue, Decisions #11.)

### Task 8.1: `OutgoingMessageQueue` (TDD)

Used by `TwitchIrcChatService` (Plan B); fully unit-testable here with `FakeClock` + `FakeTimerScheduler` + a captured-sends list.

**Files:**
- Create: `src/Ti/Chat/Internal/OutgoingMessageQueue.cs`
- Test: `tests/Chat/Internal/OutgoingMessageQueueTests.cs`

- [ ] **Step 1: Create the failing test**

`tests/Chat/Internal/OutgoingMessageQueueTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.Internal;

public class OutgoingMessageQueueTests {
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTimerScheduler _scheduler;
    private readonly List<string> _sent = new();

    public OutgoingMessageQueueTests() {
        _scheduler = new FakeTimerScheduler(_clock);
    }

    private OutgoingMessageQueue New(int capacity = 90, TimeSpan? window = null) {
        return new OutgoingMessageQueue(
            capacity: capacity,
            window: window ?? TimeSpan.FromSeconds(30),
            clock: _clock,
            scheduler: _scheduler,
            send: s => { _sent.Add(s); return Task.CompletedTask; });
    }

    [Fact]
    public async Task SingleEnqueue_SendsImmediately_WhenTokensAvailable() {
        var q = New();
        await q.EnqueueAsync("hi", OutgoingMessagePriority.Normal);
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Single(_sent);
        Assert.Equal("hi", _sent[0]);
    }

    [Fact]
    public async Task PriorityOrder_HighBeforeNormalBeforeLow() {
        var q = New(capacity: 1, window: TimeSpan.FromSeconds(30));
        // Burst: 3 messages enqueued at once (low/normal/high)
        await q.EnqueueAsync("low",  OutgoingMessagePriority.Low);
        await q.EnqueueAsync("norm", OutgoingMessagePriority.Normal);
        await q.EnqueueAsync("high", OutgoingMessagePriority.High);
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Single(_sent);
        Assert.Equal("high", _sent[0]);     // first token spent on highest-priority

        _scheduler.Advance(TimeSpan.FromSeconds(30));   // window resets, 1 more token
        Assert.Equal(2, _sent.Count);
        Assert.Equal("norm", _sent[1]);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(3, _sent.Count);
        Assert.Equal("low", _sent[2]);
    }

    [Fact]
    public async Task LowCoalescesStale_WhenAnotherLowEnqueued() {
        var q = New(capacity: 0, window: TimeSpan.FromSeconds(30));   // 0 tokens — nothing sends yet
        await q.EnqueueAsync("tally1", OutgoingMessagePriority.Low);
        await q.EnqueueAsync("tally2", OutgoingMessagePriority.Low);
        await q.EnqueueAsync("tally3", OutgoingMessagePriority.Low);
        // Now allow one send.
        _clock.Advance(TimeSpan.FromSeconds(30));
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Single(_sent);
        Assert.Equal("tally3", _sent[0]);   // stale tally1, tally2 dropped; only latest-Low survives
    }

    [Fact]
    public async Task RateLimit_NeverExceedsCapacityPerWindow() {
        var q = New(capacity: 3, window: TimeSpan.FromSeconds(30));
        for (int i = 0; i < 10; i++)
            await q.EnqueueAsync($"m{i}", OutgoingMessagePriority.Normal);

        _scheduler.Advance(TimeSpan.FromSeconds(0));
        Assert.Equal(3, _sent.Count);   // capacity exhausted

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(6, _sent.Count);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(9, _sent.Count);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(10, _sent.Count);   // last one drains
    }

    [Fact]
    public async Task DrainAsync_FlushesPendingMessages_RespectingRateLimit() {
        var q = New(capacity: 2, window: TimeSpan.FromSeconds(30));
        await q.EnqueueAsync("a", OutgoingMessagePriority.Normal);
        await q.EnqueueAsync("b", OutgoingMessagePriority.Normal);
        await q.EnqueueAsync("c", OutgoingMessagePriority.Normal);
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Equal(2, _sent.Count);

        var drainTask = q.DrainAsync();
        _scheduler.Advance(TimeSpan.FromSeconds(30));
        await drainTask;
        Assert.Equal(3, _sent.Count);
    }
}
```

- [ ] **Step 2: Run to verify failures**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~OutgoingMessageQueueTests"`
Expected: build error.

- [ ] **Step 3: Create `src/Ti/Chat/Internal/OutgoingMessageQueue.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// Outgoing-message buffer with token-bucket rate limiting + priority
/// ordering + stale-Low coalescing. Per-window token budget refills at
/// the start of each window. High > Normal > Low when picking the next
/// send. New Low enqueues evict any older queued Low (stale tallies).
/// </summary>
public sealed class OutgoingMessageQueue : IDisposable {
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly Func<string, Task> _send;

    private readonly Queue<string> _high = new();
    private readonly Queue<string> _normal = new();
    private readonly Queue<string> _low = new();   // single-element-effective due to coalescing

    private DateTimeOffset _windowStart;
    private int _tokens;
    private TaskCompletionSource? _drainTcs;
    private bool _disposed;

    public OutgoingMessageQueue(
        int capacity, TimeSpan window,
        IClock clock, ITimerScheduler scheduler,
        Func<string, Task> send) {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _capacity = capacity; _window = window;
        _clock = clock; _scheduler = scheduler; _send = send;
        _windowStart = clock.UtcNow;
        _tokens = capacity;
        // Tick at every window boundary (refill) and on enqueue (Pulse).
        scheduler.SchedulePeriodic(window, RefillAndDrain);
    }

    public Task EnqueueAsync(string message, OutgoingMessagePriority priority) {
        if (_disposed) return Task.CompletedTask;
        switch (priority) {
            case OutgoingMessagePriority.High:   _high.Enqueue(message); break;
            case OutgoingMessagePriority.Normal: _normal.Enqueue(message); break;
            case OutgoingMessagePriority.Low:    _low.Clear(); _low.Enqueue(message); break;
        }
        Drain();
        return Task.CompletedTask;
    }

    public Task DrainAsync() {
        if (_high.Count + _normal.Count + _low.Count == 0) return Task.CompletedTask;
        _drainTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _drainTcs.Task;
    }

    private void RefillAndDrain() {
        // Reset window every `_window` regardless of how many tokens were used.
        _windowStart = _clock.UtcNow;
        _tokens = _capacity;
        Drain();
    }

    private void Drain() {
        while (_tokens > 0) {
            var msg = TryDequeueHighestPriority();
            if (msg is null) break;
            _tokens--;
            _ = _send(msg);
        }
        if (_high.Count + _normal.Count + _low.Count == 0) {
            _drainTcs?.TrySetResult();
            _drainTcs = null;
        }
    }

    private string? TryDequeueHighestPriority() {
        if (_high.TryDequeue(out var h)) return h;
        if (_normal.TryDequeue(out var n)) return n;
        if (_low.TryDequeue(out var l)) return l;
        return null;
    }

    public void Dispose() {
        _disposed = true;
        _drainTcs?.TrySetCanceled();
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~OutgoingMessageQueueTests"`
Expected: `Passed: 5, Failed: 0`.

- [ ] **Step 5: Run the FULL suite to confirm nothing regressed**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all tests across all phases passing. Roughly:

| Phase | Test class | Expected count |
|---|---|---|
| 0 | `_Sanity` | 1 |
| 1 | `FakeClockTests` | 3 |
| 1 | `FakeTimerSchedulerTests` | 4 |
| 1 | `TiLogTests` | 4 |
| 1 | `ImmediateDispatcherTests` | 3 |
| 2 | `ChatCredentialsTests` | 7 |
| 2 | `ChatMessageTests` | 2 |
| 3 | `FakeChatServiceTests` | 8 |
| 4 | `EnglishReceiptsTests` | 7 |
| 5 | `VoteSessionTests` | 53 |
| 6 | `VoteCoordinatorTests` | 7 |
| 6 | `VoterTests` | 2 |
| 7 | `TwitchIrcParserTests` | 19 |
| 8 | `OutgoingMessageQueueTests` | 5 |
| **Total** | | **125** |

Actual count may shift by a few as you discover edge cases worth covering, but the order of magnitude is right.

- [ ] **Step 6: Commit**

```powershell
git add src/Ti/Chat/Internal/OutgoingMessageQueue.cs tests/Chat/Internal/OutgoingMessageQueueTests.cs
git commit -m @'
plan-a/8.1: OutgoingMessageQueue — token bucket + priority + Low coalescing

5 tests covering: single enqueue with available tokens sends immediately;
priority ordering (High > Normal > Low) when bursting; new Low entries
evict older Low (stale-tally coalescing); rate limit never exceeds
capacity per window across many enqueues; DrainAsync waits for pending
sends respecting the rate limit.

Plan A: full TI core library with deterministic test coverage. Plan B
will build TwitchIrcChatService on top of this + TwitchIrcParser.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Self-review

- **Spec coverage**: every Plan A type from the v2.2 spec's `Ti/Internal`, `Ti/Chat`, `Ti/Voting` folders is implemented. The Godot-dependent types (`Ti/Godot/`, `Ti/Ui/`) and the real IRC client (`TwitchIrcChatService`) are explicitly deferred to Plan B; flagged in the Plan B section below.
- **Spec features mapped to tasks**:
  - 0-indexed options (Decision #14): tasks 5.1, 5.2, 6.1.
  - Latest-vote-wins (Decision #4): task 5.2.
  - Tie-break + no-voter random (Decisions #6, #7): task 5.3.
  - State machine + CloseNow / Cancel / Dispose (Decision #7, R3/R4/R5): task 5.4.
  - Adaptive cadence + receipts (Optional Enhancement #1): task 5.5.
  - AwaitWinnerAsync + no-await Warn (Optional Enhancement #12): task 5.6.
  - Disconnect-tolerance (Spec §Disconnect tolerance): task 5.7.
  - Voter dict cap (Spec §Tally rules): task 5.8.
  - Instance VoteCoordinator + static Voter (Decision #9): tasks 6.1, 6.2.
  - IRC parser including IRCv3 escaping + protocol matrix (Decision #12, Spec §IRC protocol handling matrix): tasks 7.1–7.3.
  - Outgoing send queue + rate limit + priority + coalescing (Decision #11): task 8.1.
  - VoteParsingPolicy with `!N` toggle (Decision #8 + parser update): task 4.1, exercised in 5.2.
  - 0-indexed receipts including 2-tie / 3+-tie distinction (Optional Enhancement #13): task 4.2.
  - Channel URL parsing (Optional Enhancement #14): land at the `TwitchIrcChatService.ConnectAsync` boundary in Plan B; no Plan A type touches the channel string.
  - oauth-prefix normalisation (Optional Enhancement #11): task 2.2.
  - VoteOption internal ctor (Optional Enhancement #15): task 4.1.
- **Placeholder scan**: no "TBD"/"TODO"/"implement later" steps. Every task shows full code or full test code. The `EnglishReceipts.FormatClose` 2-tie path's `Tallies.Where(...)` query reads tied options from `VoteSnapshot.Tallies` directly — checked.
- **Type consistency**: `VoteSession.OnChatMessage` reads `msg.VoterKey`; `ChatMessage.VoterKey` exists (task 2.3). `VoteCoordinator.Start` builds `List<VoteOption>` via the internal ctor (task 6.1); `VoteOption` ctor is internal (task 4.1) — same assembly so this works. `TwitchIrcParser.ParsePrivmsg` constructs `ChatMessage(UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip)` matching the task 2.3 record signature.
- **Scope**: focused on the v2.2 spec's `Ti/{Internal,Chat,Voting}` triad. Plan B handles the Godot-dependent pieces and the real IRC client.

## Plan B preview (next plan)

To be written after Plan A is implemented and reviewed. Approximate task list:

1. `Ti/Godot/GodotMainThreadDispatcher.cs` — `IMainThreadDispatcher` impl that drains a `ConcurrentQueue<Action>` from a long-lived autoload `Node`'s `_Process`. Includes `DispatcherAutoload.cs` registration.
2. `Ti/Chat/TwitchIrcChatService.cs` — handcrafted Twitch IRC client using `TcpClient` + `SslStream` + `StreamReader.ReadLineAsync`, the `TwitchIrcParser` for incoming lines, and the `OutgoingMessageQueue` for sends. Implements connect/disconnect/reconnect-with-jitter/auth-failure-terminal/self-echo-filter/anonymous-mode. Channel normalisation (Optional Enhancement #14) lives here.
3. `Ti/Ui/VoteOverlayControl.cs` — Godot `Control` consuming `VoteSession`; `_Process`-driven redraw; auto-hide on close.
4. `Ti/Ui/ChatStatusControl.cs` — Godot `Control` consuming `IChatService`; reads `LastMessageReceivedAt` + `State` for diagnostic display (Optional Enhancement #9).
5. `ModEntry.cs` — `[ModInitializer("Initialize")]` wires up `DispatcherAutoload` → `TwitchIrcChatService` → `VoteCoordinator` → sets `Voter.Default`. Sets `TiLog.Sink` to wrap `MegaCrit.Sts2.Core.Logging.Log`.
6. `build.ps1` — extend with Godot `--build-solutions --quit --headless` invocation and an `install.ps1` that copies outputs to `<game-install>/mods/slay_the_streamer_2/`.
7. Optional: a small `Game/DecisionVotes/SmokeVotePatch.cs` Harmony patch on a non-critical game method that fires a 5s vote on game launch — proves the end-to-end loop in-game without touching the actual decision points.

## Plan C preview (small standalone)

`tools/irc-fixture-generator/` — a separate console csproj using `Microsoft.NET.Sdk` (no Godot). Uses Plan A's `TwitchIrcChatService` (Plan B will add it), `ImmediateDispatcher`, and a captured-line writer. Run once at bootstrap to seed `tests/Chat/Internal/Fixtures/irc-corpus-YYYY-MM-DD.json`; rerun ad-hoc when Twitch ships an IRC quirk that breaks the parser. (Optional Enhancement #6.)

---

**Plan complete and saved to `docs/superpowers/plans/2026-05-08-ti-core-implementation.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Each subagent has minimal context (one task) so output stays focused. We checkpoint between tasks.

**2. Inline Execution** — I execute tasks in this session using the `executing-plans` skill, batch execution with checkpoints for your review.

**Which approach?**









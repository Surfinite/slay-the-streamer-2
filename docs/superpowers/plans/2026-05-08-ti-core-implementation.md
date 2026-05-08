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

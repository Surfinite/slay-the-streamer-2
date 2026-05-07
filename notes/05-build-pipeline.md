# Build pipeline & deployment

Source: `references/STS2FirstMod/` — the working example by jiegec
(adapted from doctornoodlearms's [reddit guide](https://www.reddit.com/r/slaythespire/comments/1rm5gvg/sts2_early_access_mod_guide/)).

## TL;DR

```
┌──────────────────┐
│  Godot 4.5.1     │  builds C# code via Godot.NET.Sdk
│  Mono build      │  produces .dll under .godot/mono/temp/bin/Debug/
└──────────────────┘
        │
        v
┌──────────────────┐
│  Godot export    │  packages res:// assets (optional per manifest)
│  --export-pack   │  produces .pck
└──────────────────┘
        │
        v
   ┌─────────┐
   │  dist/  │  contains: <id>.dll, <id>.pck (optional), <id>.json
   └─────────┘
        │
        v copy to <game-install>/mods/<id>/
```

## Prerequisites we don't yet have

| Tool | Purpose | Status |
|---|---|---|
| .NET 9 SDK | C# compilation | ✅ installed (9.0.313) |
| sts2.dll reference | API surface for our mod | ✅ located |
| Godot 4.5.1 Mono (.NET) | Build invocation + asset packaging | ✅ installed at `C:\Tools\Godot_4.5.1_mono\` |
| ILSpy CLI | Decompile reference | ✅ installed |

**Godot executables** (after extraction):
- `C:\Tools\Godot_4.5.1_mono\Godot_v4.5.1-stable_mono_win64.exe` — GUI
- `C:\Tools\Godot_4.5.1_mono\Godot_v4.5.1-stable_mono_win64_console.exe` — console (use this for `--build-solutions` etc., it returns stdout properly)
- Verified: prints `4.5.1.stable.mono.official.f62fdbde1` on `--version`.

## Project structure (target for our mod)

```
slay-the-streamer-2/
├── src/                       (mod source)
│   ├── slay_the_streamer_2.csproj
│   ├── ModEntry.cs            ([ModInitializer] class)
│   ├── ...                    (other .cs files)
│   ├── slay_the_streamer_2.json    (manifest)
│   ├── icon.svg               (Godot project icon, low effort)
│   ├── project.godot          (Godot project config)
│   ├── export_presets.cfg     (Godot export presets)
│   └── sts2.dll               (copied from game install before each build)
├── build.ps1                  (Windows build script — equiv to STS2FirstMod's build.sh)
├── install.ps1                (copy to game's mods folder)
└── dist/                      (build output, gitignored)
```

(The exact mod-id slug is TBD — `slay_the_streamer_2` is a placeholder.)

## The csproj file

Minimal viable, from the reference example:

```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="sts2">
      <HintPath>sts2.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

**Why `Godot.NET.Sdk` not `Microsoft.NET.Sdk`?**
- Required if our code calls Godot APIs (UI rendering, input, scene tree).
  Almost certainly we'll need this for vote-tally overlays and similar.
- The Godot SDK pulls in `Godot.SourceGenerators` and the right runtime
  references for Godot interop (signals, autoloads, etc.).
- A pure-C# behaviour mod (e.g., a relic balance tweak) could theoretically
  use `Microsoft.NET.Sdk` and skip the Godot dependency, but we're not that.

**`EnableDynamicLoading=true`** is required because the game loads our DLL via
`AssemblyLoadContext.LoadFromAssemblyPath` rather than as a regular project
reference.

## The manifest file

Concrete example from STS2FirstMod:

```json
{
    "id": "FirstMod",
    "name": "FirstMod",
    "author": "doctornoodlearms",
    "description": "",
    "version": "1.0.0",
    "has_pck": true,
    "has_dll": true,
    "dependencies": [],
    "affects_gameplay": true
}
```

Our v0.1 manifest will probably look like:

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

(`has_pck: false` for v0.1 if we ship no custom assets. Easy to flip later.)

## Mod entry point (concrete)

The simplest possible `[ModInitializer]` from the reference:

```csharp
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;

namespace FirstMod;

[ModInitializer("ModLoaded")]
public static class FirstMod {
    public static void ModLoaded() {
        Log.Warn("MOD FINISHED LOADING");
    }
}
```

For our v0.1, the entry point will do considerably more:

```csharp
[ModInitializer("Initialize")]
public static class StreamerMod {
    public static void Initialize() {
        // 1. Apply Harmony patches in our assembly
        new Harmony("surfinite.slay_the_streamer_2").PatchAll(Assembly.GetExecutingAssembly());

        // 2. Subscribe to run-state hooks (only if we use AbstractModel layer for sealed deck etc.)
        ModHelper.SubscribeForRunStateHooks("slay_the_streamer_2", runState =>
            new AbstractModel[] { /* our run-level models */ });

        // 3. Start IRC client + vote engine
        ChatService.Start();
        VoteEngine.Start();

        Log.Info("Slay the Streamer 2: loaded");
    }
}
```

*Note:* could rely on the auto-`PatchAll` fallback (no `[ModInitializer]`), but
we need the `[ModInitializer]` for IRC startup, so explicit Harmony call it is.

## Build script (Windows)

The reference `build.sh` is bash. Our Windows equivalent will live as
`build.ps1` and roughly do:

```powershell
$gameDll = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"
$godot   = "C:\Tools\Godot_4.5.1_mono\Godot_v4.5.1-stable_mono_win64_console.exe"

Copy-Item $gameDll src\sts2.dll
& $godot --build-solutions --quit --headless --path src
Remove-Item -Recurse -Force dist
New-Item -ItemType Directory dist | Out-Null
Copy-Item src\.godot\mono\temp\bin\Debug\slay_the_streamer_2.dll dist\
# If has_pck=true, also export the .pck:
# & $godot --export-pack "Windows Desktop" dist\slay_the_streamer_2.pck --headless --path src
Copy-Item src\slay_the_streamer_2.json dist\
```

Plus an `install.ps1` to copy `dist\*` into
`<game-install>\mods\slay_the_streamer_2\`.

## Runtime debugging

Godot's Debug Server is built into the engine and supports remote attach over TCP:

1. In Godot editor: `Debug → Keep Debug Server Open`.
2. In Steam (Slay the Spire 2 launch options):
   `--remote-debug tcp://127.0.0.1:6007`
3. Launch the game.

The reference doesn't fully document the debugger workflow but mentions this
as the official approach. We'll figure out the exact attach steps the first
time we hit a real bug.

For lighter-weight debugging, `Log.Info(...)` etc. via
`MegaCrit.Sts2.Core.Logging.Log` writes to the game's log file. Decompile
shows logs go to Godot's standard logger.

## Things to confirm at design time

- Whether `has_pck: false` works correctly for code-only mods (the example
  always uses both DLL and PCK). The decompile says they're independent —
  trust that for now, verify when we build first.
- Exact location of game logs on Windows (presumably `%APPDATA%/Slay the Spire 2/`
  or similar) — useful for reading our `Log.Info` output.
- Whether `Godot --build-solutions` *requires* a project.godot to coexist
  with the .csproj, or if we can use a pure dotnet flow. (The csproj uses
  `Godot.NET.Sdk` so Godot context is probably required.)

## All prerequisites in place

(2026-05-07: Godot was installed in the same session — see toolchain table above.)
We're fully ready to start building once we've finished design.

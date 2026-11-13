# refcheck

Checks a built mod DLL against game DLLs without running anything. One mod DLL has to load on several game versions. A reference that compiles against one version can still throw `MissingMethodException`, `MissingFieldException` or `TypeLoadException` on another. So can a Harmony target that is bound by name. refcheck finds these by reading metadata and IL. It uses only `System.Reflection.Metadata` and never loads an assembly for execution.

## Run it

```powershell
pwsh -File tools\refcheck\run.ps1                       # dist\ build vs every decompiled\dll-archive\sts2-*.dll
pwsh -File tools\refcheck\run.ps1 -ModDll <path.dll>    # any mod DLL
pwsh -File tools\refcheck\run.ps1 -TextualTree decompiled\sts2-v0.107.1   # also a version with no DLL on disk
```

`run.ps1` works from any directory and builds the tool when its sources change (`net9.0`, under the repo's `global.json` pin). It prints one summary per game DLL, plus how every MemberCompat / FieldRefAccess lookup resolved. Full reports and a per-version reference dump go to `tools/refcheck/out/`. The exit code is 1 when there is a real failure (see Severity), otherwise 0.

The Steam game folder supplies `GodotSharp.dll` and the BCL, so base-type walks can continue past `sts2` (`--ext`). `-NoExt` turns that off.

Direct use: `refcheck <mod.dll> <sts2.dll> [--dump <file>] [--ext <dir>]` and `refcheck --textual <dump> <decompiledTree> [--out <file>]`.

## What it checks

1. **Compiled references.** It checks every TypeRef and MemberRef into `sts2`: type present, member with the exact signature present (including return type and generic arity), and accessible. Member lookups walk the `sts2` base chain. When a member is missing, it lists the same-name members the game does have and the mod methods that use the reference.
2. **Mod types that derive from game types.** It checks for abstract members and interface methods the mod type doesn't implement (`Assembly.GetTypes()` fails on these). It also finds `override`s whose base method changed shape; those silently stop overriding.
3. **Harmony targets.** `[HarmonyPatch]` attributes are resolved with Harmony's rules. `TargetMethod()`/`TargetMethods()` return values are evaluated by a small IL interpreter that follows:
   - `static readonly Lazy<...>` fields into their `<>c` lambdas;
   - getters on mod helpers (`CardRewardCtors.WithPlayer`);
   - `List.Add`, `yield return`, and `foreach` over literal name arrays.

   Every resolved target then gets a check that each Prefix/Postfix/Finalizer parameter binds by name, including `___field` injections.
4. **By-name lookups anywhere in the mod**, including lambdas and static initialisers:
   - `MemberCompat.FirstMethod/FirstField`: names are tried in order; the report shows which name won and whether later names also exist;
   - `MemberCompat.ConstructorsWithParameter`, `Construct` (shows the chosen constructor) and `HasParameter` (informational);
   - `AccessTools.Method/Field/Property/...`;
   - `AccessTools.FieldRefAccess<T,F>("name")`, where the field type must be assignable to F;
   - `Type.GetMethod/GetField/GetProperty`.

## Severity

| Tag | Meaning | Fails the run |
|---|---|---|
| FAIL | A compiled reference doesn't bind, or there is a load-breaking type gap. A Harmony target or parameter doesn't bind in a class **without** `Prepare` (Harmony throws during `PatchAll`, and every later patch class is skipped). Or a lookup that must resolve doesn't: `Construct` and `FieldRefAccess` (both throw), a `FirstMethod`/`FirstField` that lists **several** names (alternatives were declared but none matches), or `ConstructorsWithParameter` with no match. | yes |
| GATED | A Harmony class **with** `Prepare` whose attribute target or parameter binding doesn't work on this version. The class is expected to skip itself, so that feature is off on this version. | no |
| OPTIONAL | Plain `AccessTools` lookups and single-name `FirstMethod`/`FirstField` lookups that return null and are null-checked. Also targets that the code filters into a list, or that a `Prepare` gate covers. These are expected to be absent on some versions. | no |
| UNDECIDED | A target that can't be evaluated statically. The report gives the reason. | no |

**Read the OPTIONAL list on the newest game version after every game update.** A single-name lookup whose member was renamed (for example `FirstField(..., "_currentRelics")`) shows as OPTIONAL, not FAIL, because the tool cannot tell it from a lookup that is absent on purpose. On the current Beta the expected count is 0; anything listed there is either a member that exists only on a newer version than the one being checked, or a rename to chase.

## Known limits

- Only references into `sts2` are compared. GodotSharp, BCL and Harmony references aren't, because there is only one version of each on disk.
- The comparison doesn't see static-vs-instance changes on fields, class/struct changes, generic constraints, or values that were inlined at compile time (enum constants, `const` fields, default values of optional parameters).
- The IL interpreter walks code in order and doesn't model branches. A ternary in a lookup argument, or a type or name computed at runtime, is reported as dynamic or undecided, not guessed. Lookups on `obj.GetType()` use the static type of `obj`.
- Harmony parameter binding compares names only, not types or `ref`. `MethodType.Enumerator`/`Async` targets are resolved by name only.
- The severity rules decide from code shape (whether there is a `Prepare`, how many names a lookup lists, whether results go into a list). They can't read intent. A feature that is GATED or OPTIONAL is still off on that version.
- `--textual` works at regex level: simple type names, nullable annotations ignored. On the v0.111.0 and v0.113.0 trees it reproduced the binary verdicts exactly. Treat it as a pointer to read the source, not as proof.

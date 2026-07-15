# Crash-dump evidence: sentry-godot modded-teardown crash

Supporting evidence for [megacrit/sts2-mod-uploader#14](https://github.com/megacrit/sts2-mod-uploader/issues/14)
(Windows: intermittent silent crash at modded startup — use-after-free in sentry-godot
`should_sample` callable after `SentryService` modded shutdown).

Dumps were written by the game's crashpad handler to
`%APPDATA%\SlayTheSpire2\sentry\reports\` and copied here 2026-07-15 because the game
prunes that folder — one of the original five (`f05d7277…`, v0.107.1, 04:23:19) was
already deleted by the game within hours of the crash.

## Inventory

| Dump | Crash time (2026-07-15) | Game build |
|---|---|---|
| `5a024ac0-….dmp` | 04:23:59 | v0.107.1 (non-beta branch) |
| `5b64e628-….dmp` | 04:27:13 | v0.108.0 (public-beta, commit 58694f64) |
| `e9565e6b-….dmp` | 04:27:23 | v0.108.0 |
| `738b210c-….dmp` | 04:31:37 | v0.108.0 |
| `f05d7277-….dmp` | 04:23:19 | v0.107.1 — **pruned by the game before it could be copied**; stack was captured first and is identical to the others |

All five had the identical `!analyze -v` bucket:

```
INVALID_POINTER_READ_c0000005_SlayTheSpire2.exe!Object::get_instance_id
```

Faulting stack (identical across dumps, modulo module base addresses):

```
SlayTheSpire2!Object::get_instance_id                    <- AV
SlayTheSpire2!MethodBindTRC<ObjectID>::ptrcall
libsentry_windows_release_x86_64 (+0x569f4 / +0x28a42 / +0x6562e)
SlayTheSpire2!CallableCustomExtension::is_valid
SlayTheSpire2!Object::emit_signalp
SlayTheSpire2!SceneTree::process
SlayTheSpire2!Main::iteration
```

## Re-analyzing

cdb ships with WinDbg (`winget install Microsoft.WinDbg`):

```
cdbX64.exe -z <dump.dmp> -c "!analyze -v; k 40; q"
```

The `.dmp` files are gitignored (binary, ~2.7 MB each); this README is tracked so the
context survives even if the local copies are lost.

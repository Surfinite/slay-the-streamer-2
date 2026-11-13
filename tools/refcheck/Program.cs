using RefCheck;

// refcheck <modDll> <gameDll> [--dump <file>] [--ext <dir>]
//   Binary-compatibility check of <modDll> against <gameDll>: every compiled TypeRef/MemberRef
//   into the game assembly, mod types deriving from game types, Harmony patch targets and
//   parameter binding, and every by-name reflection lookup (MemberCompat, AccessTools, Type.GetX).
//   --ext <dir>: also resolve base types outside the game assembly (GodotSharp, BCL) from <dir>,
//   normally the game's data_sts2_windows_x86_64 folder.
//   Exit code: 0 = no real failure, 1 = at least one FAIL, 2 = usage error.
// refcheck --textual <dumpFile> <decompiledDir> [--out <file>]
//   Textual (regex-level) check of a --dump file against a flat ILSpy-decompiled source tree,
//   for game versions with no DLL on disk. Informational: always exits 0.

if (args.Length >= 1 && args[0] == "--textual")
{
    if (args.Length < 3) { Console.Error.WriteLine("usage: refcheck --textual <dumpFile> <decompiledDir> [--out <file>]"); return 2; }
    string? outFile = null;
    for (int i = 3; i < args.Length; i++) if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
    return Textual.Run(args[1], args[2], outFile);
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: refcheck <modDll> <gameDll> [--dump <file>] [--ext <dir>]");
    Console.Error.WriteLine("       refcheck --textual <dumpFile> <decompiledDir> [--out <file>]");
    return 2;
}
string? dump = null, ext = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
    else if (args[i] == "--ext" && i + 1 < args.Length) ext = args[++i];
}
var mod = new Asm(Path.GetFullPath(args[0]));
var game = new Asm(Path.GetFullPath(args[1]));
var set = new AsmSet(game, ext);
var checker = new Checker(mod, set, Console.Out);
checker.Run();
if (dump != null)
{
    checker.WriteDump(dump);
    Console.WriteLine($"dump written: {Path.GetFullPath(dump)}");
}
return checker.AnyFail ? 1 : 0;

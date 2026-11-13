using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RefCheck;

/// <summary>One metadata-only loaded assembly (never loaded for execution).</summary>
public sealed class Asm
{
    public readonly string Path;
    public readonly PEReader Pe;
    public readonly MetadataReader R;
    public readonly string Name;
    public readonly Dictionary<string, TypeDefinitionHandle> Types = new(StringComparer.Ordinal);
    /// <summary>Exported (forwarded) types: full name -> target assembly name.</summary>
    public readonly Dictionary<string, string> Forwarders = new(StringComparer.Ordinal);

    public Asm(string path)
    {
        Path = path;
        Pe = new PEReader(File.OpenRead(path));
        R = Pe.GetMetadataReader();
        Name = R.IsAssembly ? R.GetString(R.GetAssemblyDefinition().Name) : System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var h in R.TypeDefinitions)
            Types[Names.TypeDefFullName(R, h)] = h;
        foreach (var eh in R.ExportedTypes)
        {
            var et = R.GetExportedType(eh);
            var full = ExportedFullName(eh);
            var impl = et.Implementation;
            while (impl.Kind == HandleKind.ExportedType) impl = R.GetExportedType((ExportedTypeHandle)impl).Implementation;
            if (impl.Kind == HandleKind.AssemblyReference)
                Forwarders[full] = R.GetString(R.GetAssemblyReference((AssemblyReferenceHandle)impl).Name);
        }
    }

    string ExportedFullName(ExportedTypeHandle h)
    {
        var et = R.GetExportedType(h);
        var name = R.GetString(et.Name);
        if (et.Implementation.Kind == HandleKind.ExportedType)
            return ExportedFullName((ExportedTypeHandle)et.Implementation) + "/" + name;
        var ns = R.GetString(et.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string TypeName(TypeDefinitionHandle h) => Names.TypeDefFullName(R, h);
}

/// <summary>The game assembly plus (optionally) the other game-side assemblies used to
/// continue base-type walks that leave sts2.</summary>
public sealed class AsmSet
{
    public readonly Asm Game;
    readonly string? _extDir;
    readonly Dictionary<string, Asm?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AsmSet(Asm game, string? extDir)
    {
        Game = game;
        _extDir = extDir;
        _cache[game.Name] = game;
    }

    public bool HasExt => _extDir != null;

    public Asm? Load(string name)
    {
        if (_cache.TryGetValue(name, out var a)) return a;
        Asm? loaded = null;
        if (_extDir != null)
        {
            var p = Path.Combine(_extDir, name + ".dll");
            if (File.Exists(p)) { try { loaded = new Asm(p); } catch { loaded = null; } }
        }
        _cache[name] = loaded;
        return loaded;
    }

    /// <summary>Resolve fullName in assembly asmName, following type forwarders.</summary>
    public (Asm asm, TypeDefinitionHandle h)? Resolve(string asmName, string fullName, int depth = 0)
    {
        if (depth > 8) return null;
        var a = Load(asmName);
        if (a == null) return null;
        if (a.Types.TryGetValue(fullName, out var h)) return (a, h);
        // a nested type is forwarded with its outer type
        var outer = fullName.Split('/')[0];
        if (a.Forwarders.TryGetValue(fullName, out var target) || a.Forwarders.TryGetValue(outer, out target))
            return Resolve(target, fullName, depth + 1);
        return null;
    }
}

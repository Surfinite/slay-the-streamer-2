using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace RefCheck;

public enum St
{
    Pass,
    PassViaBase,          // found on an sts2 base type / inherited interface
    PassExternal,         // found on a base type outside sts2 (only with --ext)
    TypeMissing,
    MemberMissing,
    SignatureMismatch,
    AccessFail,           // resolved, but the game member/type is not public/protected
    UnverifiableExternalBase,
    Skipped,              // e.g. runtime-provided array methods
}

public sealed class LookupResult
{
    public St Status;
    public string? FoundOn;
    public string? Access;
    public readonly List<string> Candidates = new();
    public string? Note;
}

public readonly record struct ChainLink(Asm Asm, TypeDefinitionHandle H, string[]? Ctx, int Depth);

public static class Resolver
{
    public static string MethodKey(MethodSignature<string> s)
    {
        var sb = new StringBuilder();
        if (s.Header.IsInstance) sb.Append("instance ");
        if (s.Header.HasExplicitThis) sb.Append("explicit ");
        if (s.Header.CallingConvention != SignatureCallingConvention.Default) sb.Append(s.Header.CallingConvention.ToString().ToLowerInvariant()).Append(' ');
        sb.Append(s.ReturnType);
        if (s.GenericParameterCount > 0) sb.Append(" `").Append(s.GenericParameterCount);
        sb.Append('(').Append(string.Join(",", s.ParameterTypes)).Append(')');
        return sb.ToString();
    }

    public static string MethodDisplay(string type, string name, MethodSignature<string> s)
    {
        var sb = new StringBuilder();
        if (s.Header.IsInstance) sb.Append("instance "); else sb.Append("static ");
        if (s.Header.CallingConvention != SignatureCallingConvention.Default) sb.Append(s.Header.CallingConvention.ToString().ToLowerInvariant()).Append(' ');
        sb.Append(s.ReturnType).Append(' ').Append(type).Append("::").Append(name);
        if (s.GenericParameterCount > 0) sb.Append("<`").Append(s.GenericParameterCount).Append('>');
        sb.Append('(').Append(string.Join(",", s.ParameterTypes)).Append(')');
        return sb.ToString();
    }

    public static string FieldDisplay(string type, string name, string fieldType) => "field " + fieldType + " " + type + "::" + name;

    public static bool IsInterface(Asm a, TypeDefinitionHandle h) => (a.R.GetTypeDefinition(h).Attributes & TypeAttributes.Interface) != 0;

    /// <summary>Decode a TypeSpec that is a generic instantiation into (underlying handle, args).</summary>
    public static bool TryGenericInst(MetadataReader r, TypeSpecificationHandle tsh, string[]? ctx, out EntityHandle underlying, out string[] args)
    {
        var br = r.GetBlobReader(r.GetTypeSpecification(tsh).Signature);
        var code = br.ReadSignatureTypeCode();
        underlying = default; args = Array.Empty<string>();
        if (code != SignatureTypeCode.GenericTypeInstance) return false;
        br.ReadCompressedInteger(); // class / valuetype marker
        underlying = br.ReadTypeHandle();
        int n = br.ReadCompressedInteger();
        var dec = new SignatureDecoder<string, string[]?>(SigProvider.Instance, r, ctx);
        args = new string[n];
        for (int i = 0; i < n; i++) args[i] = dec.DecodeType(ref br);
        return true;
    }

    /// <summary>Resolve a TypeDef/TypeRef/TypeSpec handle (in asm a, under ctx) to a type definition
    /// somewhere in the set. externalName is set when the type lives outside the resolvable set.</summary>
    public static ChainLink? ResolveHandle(AsmSet set, Asm a, EntityHandle h, string[]? ctx, int depth, out string? externalName)
    {
        externalName = null;
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition:
                return new ChainLink(a, (TypeDefinitionHandle)h, null, depth);
            case HandleKind.TypeReference:
            {
                var trh = (TypeReferenceHandle)h;
                var full = Names.TypeRefFullName(a.R, trh);
                var scope = Names.TypeRefScopeAssembly(a.R, trh);
                if (scope.Length == 0) { externalName = full + " [non-assembly scope]"; return null; }
                bool isGame = string.Equals(scope, set.Game.Name, StringComparison.OrdinalIgnoreCase);
                if (!isGame && !set.HasExt) { externalName = full + " [" + scope + "]"; return null; }
                var res = set.Resolve(scope, full);
                if (res == null) { externalName = full + " [" + scope + (isGame ? ", MISSING in game" : ", not found") + "]"; return null; }
                return new ChainLink(res.Value.asm, res.Value.h, null, depth);
            }
            case HandleKind.TypeSpecification:
            {
                if (!TryGenericInst(a.R, (TypeSpecificationHandle)h, ctx, out var und, out var args))
                {
                    externalName = "[non-generic typespec]";
                    return null;
                }
                var link = ResolveHandle(set, a, und, null, depth, out externalName);
                if (link == null) return null;
                return link.Value with { Ctx = args };
            }
        }
        externalName = "[" + h.Kind + "]";
        return null;
    }

    /// <summary>Class chain starting at (a,h). The last element's base could not be resolved if
    /// externalStop is non-null.</summary>
    public static List<ChainLink> ClassChain(AsmSet set, Asm a, TypeDefinitionHandle h, string[]? ctx, out string? externalStop)
    {
        var list = new List<ChainLink>();
        externalStop = null;
        ChainLink? cur = new ChainLink(a, h, ctx, 0);
        int guard = 0;
        while (cur != null && guard++ < 64)
        {
            list.Add(cur.Value);
            var td = cur.Value.Asm.R.GetTypeDefinition(cur.Value.H);
            if (td.BaseType.IsNil) break;
            cur = ResolveHandle(set, cur.Value.Asm, td.BaseType, cur.Value.Ctx, cur.Value.Depth + 1, out var ext);
            if (cur == null) externalStop = ext;
        }
        return list;
    }

    /// <summary>Transitive interfaces of an interface type (excluding itself).</summary>
    public static List<ChainLink> InterfaceClosure(AsmSet set, ChainLink start, List<string> unresolved)
    {
        var result = new List<ChainLink>();
        var seen = new HashSet<string>();
        var q = new Queue<ChainLink>();
        q.Enqueue(start);
        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            var td = cur.Asm.R.GetTypeDefinition(cur.H);
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var ii = cur.Asm.R.GetInterfaceImplementation(ih);
                var link = ResolveHandle(set, cur.Asm, ii.Interface, cur.Ctx, cur.Depth + 1, out var ext);
                if (link == null) { unresolved.Add(ext ?? "?"); continue; }
                var k = link.Value.Asm.Name + "|" + link.Value.Asm.TypeName(link.Value.H) + "|" + string.Join(",", link.Value.Ctx ?? Array.Empty<string>());
                if (!seen.Add(k)) continue;
                result.Add(link.Value);
                q.Enqueue(link.Value);
            }
        }
        return result;
    }

    public static string AccessOf(MethodAttributes a) => (a & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.FamANDAssem => "private protected",
        MethodAttributes.Private => "private",
        _ => "privatescope",
    };

    public static string AccessOf(FieldAttributes a) => (a & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.Assembly => "internal",
        FieldAttributes.FamANDAssem => "private protected",
        FieldAttributes.Private => "private",
        _ => "privatescope",
    };

    public static bool AccessOk(string access) => access is "public" or "protected" or "protected internal";

    /// <summary>Type accessibility from outside the assembly (walks enclosing types).</summary>
    public static string TypeAccess(Asm a, TypeDefinitionHandle h)
    {
        var td = a.R.GetTypeDefinition(h);
        var vis = td.Attributes & TypeAttributes.VisibilityMask;
        string mine = vis switch
        {
            TypeAttributes.Public => "public",
            TypeAttributes.NotPublic => "internal",
            TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedFamily => "protected",
            TypeAttributes.NestedFamORAssem => "protected internal",
            TypeAttributes.NestedAssembly => "internal",
            TypeAttributes.NestedFamANDAssem => "private protected",
            TypeAttributes.NestedPrivate => "private",
            _ => "?",
        };
        var decl = td.GetDeclaringType();
        if (!decl.IsNil)
        {
            var outer = TypeAccess(a, decl);
            if (!AccessOk(outer)) return outer + " (enclosing)";
        }
        return mine;
    }

    public static IEnumerable<(string name, string key, string display, string access, bool isVirtual, bool isAbstract, bool newSlot, MethodDefinitionHandle h)>
        Methods(ChainLink l)
    {
        var r = l.Asm.R;
        var typeName = l.Asm.TypeName(l.H);
        foreach (var mh in r.GetTypeDefinition(l.H).GetMethods())
        {
            var md = r.GetMethodDefinition(mh);
            var name = r.GetString(md.Name);
            MethodSignature<string> sig;
            try { sig = md.DecodeSignature(SigProvider.Instance, l.Ctx); }
            catch (Exception e) { sig = new MethodSignature<string>(default, "?undecodable:" + e.Message, 0, 0, ImmutableArray<string>.Empty); }
            yield return (name, MethodKey(sig), MethodDisplay(typeName, name, sig), AccessOf(md.Attributes),
                (md.Attributes & MethodAttributes.Virtual) != 0, (md.Attributes & MethodAttributes.Abstract) != 0,
                (md.Attributes & MethodAttributes.NewSlot) != 0, mh);
        }
    }

    public static IEnumerable<(string name, string type, string display, string access)> Fields(ChainLink l)
    {
        var r = l.Asm.R;
        var typeName = l.Asm.TypeName(l.H);
        foreach (var fh in r.GetTypeDefinition(l.H).GetFields())
        {
            var fd = r.GetFieldDefinition(fh);
            var name = r.GetString(fd.Name);
            string ft;
            try { ft = fd.DecodeSignature(SigProvider.Instance, l.Ctx); } catch (Exception e) { ft = "?undecodable:" + e.Message; }
            yield return (name, ft, FieldDisplay(typeName, name, ft), AccessOf(fd.Attributes));
        }
    }

    /// <summary>Find a member by name + canonical key on the type, its base chain, and (for
    /// interfaces) inherited interfaces.</summary>
    public static LookupResult FindMember(AsmSet set, ChainLink start, string name, bool isMethod, string key)
    {
        var res = new LookupResult();
        var levels = new List<ChainLink>();
        string? externalStop = null;
        if (isMethod && name is ".ctor" or ".cctor")
        {
            // constructors are never inherited: resolve on the exact type only
            levels.Add(start);
        }
        else if (IsInterface(start.Asm, start.H))
        {
            levels.Add(start);
            var unresolved = new List<string>();
            levels.AddRange(InterfaceClosure(set, start, unresolved));
            if (unresolved.Count > 0) externalStop = string.Join("; ", unresolved);
        }
        else
        {
            levels.AddRange(ClassChain(set, start.Asm, start.H, start.Ctx, out externalStop));
        }

        foreach (var l in levels)
        {
            if (isMethod)
            {
                foreach (var m in Methods(l))
                {
                    if (m.name != name) continue;
                    if (m.key == key)
                    {
                        res.FoundOn = l.Asm.TypeName(l.H) + (l.Asm == set.Game ? "" : " [" + l.Asm.Name + "]");
                        res.Access = m.access;
                        res.Status = !AccessOk(m.access) ? St.AccessFail
                            : l.Asm != set.Game ? St.PassExternal
                            : l.Depth == 0 && l.H == start.H ? St.Pass : St.PassViaBase;
                        return res;
                    }
                    res.Candidates.Add(m.display + "  {" + m.access + "}");
                }
            }
            else
            {
                foreach (var f in Fields(l))
                {
                    if (f.name != name) continue;
                    if (f.type == key)
                    {
                        res.FoundOn = l.Asm.TypeName(l.H) + (l.Asm == set.Game ? "" : " [" + l.Asm.Name + "]");
                        res.Access = f.access;
                        res.Status = !AccessOk(f.access) ? St.AccessFail
                            : l.Asm != set.Game ? St.PassExternal
                            : l.H == start.H ? St.Pass : St.PassViaBase;
                        return res;
                    }
                    res.Candidates.Add(f.display + "  {" + f.access + "}");
                }
            }
        }

        if (externalStop != null)
        {
            res.Status = St.UnverifiableExternalBase;
            res.Note = "base chain leaves the checked set at " + externalStop + (res.Candidates.Count > 0 ? " (same-name members WITH DIFFERENT signatures exist in sts2: see candidates)" : "");
            return res;
        }
        res.Status = res.Candidates.Count > 0 ? St.SignatureMismatch : St.MemberMissing;
        return res;
    }
}

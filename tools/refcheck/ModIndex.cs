using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RefCheck;

/// <summary>One decoded IL instruction. Num carries ldc.i4.s/ldc.i4 constants and
/// ldloc/stloc/ldarg (.s and long form) indices.</summary>
public readonly record struct Instr(int Offset, ushort Op, EntityHandle Token, string? Str, long Num = 0);

/// <summary>Index of the mod assembly: IL token users, signature/structural uses, and decoded
/// custom attributes.</summary>
public sealed class ModIndex
{
    public readonly Asm Mod;
    MetadataReader R => Mod.R;

    /// <summary>token handle -> set of "Type::Method" whose IL references it directly.</summary>
    public readonly Dictionary<EntityHandle, SortedSet<string>> TokenUsers = new();
    /// <summary>method display -> TypeRefs mentioned in its local variable signature.</summary>
    public readonly Dictionary<string, HashSet<TypeReferenceHandle>> LocalMentions = new();
    /// <summary>MethodDef -> decoded IL instruction list (only token / string operands kept).</summary>
    public readonly Dictionary<MethodDefinitionHandle, List<Instr>> Il = new();
    /// <summary>TypeRef -> structural uses (base/interface/field/signature/property/constraint/attribute).</summary>
    public readonly Dictionary<TypeReferenceHandle, SortedSet<string>> StructuralUses = new();
    /// <summary>MemberRef -> structural uses (MethodImpl explicit overrides, attribute ctors).</summary>
    public readonly Dictionary<MemberReferenceHandle, SortedSet<string>> MemberStructuralUses = new();
    /// <summary>Decoded custom attributes: (owner description, owner handle, attribute type, value or error).</summary>
    public readonly List<DecodedAttr> Attributes = new();

    readonly Dictionary<EntityHandle, HashSet<TypeReferenceHandle>> _mentionCache = new();

    static readonly OperandType?[] One = new OperandType?[256];
    static readonly OperandType?[] Two = new OperandType?[256];

    static ModIndex()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            if (op.Size == 1) One[(byte)op.Value] = op.OperandType;
            else Two[(byte)(op.Value & 0xFF)] = op.OperandType;
        }
    }

    public ModIndex(Asm mod)
    {
        Mod = mod;
        ScanIl();
        ScanStructure();
        ScanAttributes();
    }

    public string MethodName(MethodDefinitionHandle mh)
    {
        var md = R.GetMethodDefinition(mh);
        return Mod.TypeName(md.GetDeclaringType()) + "::" + R.GetString(md.Name);
    }

    void AddUser(EntityHandle tok, string user)
    {
        if (!TokenUsers.TryGetValue(tok, out var s)) TokenUsers[tok] = s = new SortedSet<string>(StringComparer.Ordinal);
        s.Add(user);
    }

    void AddStructural(TypeReferenceHandle t, string use)
    {
        if (!StructuralUses.TryGetValue(t, out var s)) StructuralUses[t] = s = new SortedSet<string>(StringComparer.Ordinal);
        s.Add(use);
    }

    void AddMemberStructural(MemberReferenceHandle m, string use)
    {
        if (!MemberStructuralUses.TryGetValue(m, out var s)) MemberStructuralUses[m] = s = new SortedSet<string>(StringComparer.Ordinal);
        s.Add(use);
    }

    void ScanIl()
    {
        foreach (var mh in R.MethodDefinitions)
        {
            var md = R.GetMethodDefinition(mh);
            if (md.RelativeVirtualAddress == 0) continue;
            var user = MethodName(mh);
            var body = Mod.Pe.GetMethodBody(md.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                var col = new TypeRefCollector();
                try { R.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(col, null); } catch { }
                if (col.Found.Count > 0)
                {
                    if (!LocalMentions.TryGetValue(user, out var set)) LocalMentions[user] = set = new();
                    set.UnionWith(col.Found);
                }
            }
            var il = body.GetILReader();
            var list = new List<Instr>();
            while (il.RemainingBytes > 0)
            {
                int offset = il.Offset;
                byte b = il.ReadByte();
                ushort opv = b;
                OperandType? ot;
                if (b == 0xFE) { byte b2 = il.ReadByte(); opv = (ushort)(0xFE00 | b2); ot = Two[b2]; }
                else ot = One[b];
                if (ot == null) break; // unknown opcode; stop scanning this body
                switch (ot.Value)
                {
                    case OperandType.InlineNone: list.Add(new Instr(offset, opv, default, null)); break;
                    case OperandType.ShortInlineBrTarget: il.ReadByte(); list.Add(new Instr(offset, opv, default, null)); break;
                    case OperandType.ShortInlineI: list.Add(new Instr(offset, opv, default, null, il.ReadSByte())); break;
                    case OperandType.ShortInlineVar: list.Add(new Instr(offset, opv, default, null, il.ReadByte())); break;
                    case OperandType.InlineVar: list.Add(new Instr(offset, opv, default, null, il.ReadUInt16())); break;
                    case OperandType.InlineI: list.Add(new Instr(offset, opv, default, null, il.ReadInt32())); break;
                    case OperandType.InlineBrTarget:
                    case OperandType.ShortInlineR: il.ReadInt32(); list.Add(new Instr(offset, opv, default, null)); break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: il.ReadInt64(); list.Add(new Instr(offset, opv, default, null)); break;
                    case OperandType.InlineSwitch:
                    {
                        int n = il.ReadInt32();
                        for (int i = 0; i < n; i++) il.ReadInt32();
                        list.Add(new Instr(offset, opv, default, null));
                        break;
                    }
                    case OperandType.InlineString:
                    {
                        int tok = il.ReadInt32();
                        string s = R.GetUserString(MetadataTokens.UserStringHandle(tok & 0xFFFFFF));
                        list.Add(new Instr(offset, opv, default, s));
                        break;
                    }
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.InlineSig:
                    {
                        int tok = il.ReadInt32();
                        var h = MetadataTokens.EntityHandle(tok);
                        AddUser(h, user);
                        list.Add(new Instr(offset, opv, h, null));
                        break;
                    }
                    default: il.ReadInt32(); break;
                }
            }
            Il[mh] = list;
        }
    }

    void ScanStructure()
    {
        foreach (var th in R.TypeDefinitions)
        {
            var td = R.GetTypeDefinition(th);
            var tname = Mod.TypeName(th);
            if (!td.BaseType.IsNil)
                foreach (var t in Mentions(td.BaseType)) AddStructural(t, "base type of " + tname + " [TYPE-LOAD]");
            foreach (var ih in td.GetInterfaceImplementations())
                foreach (var t in Mentions(R.GetInterfaceImplementation(ih).Interface)) AddStructural(t, "interface of " + tname + " [TYPE-LOAD]");
            foreach (var gph in td.GetGenericParameters())
                foreach (var ch in R.GetGenericParameter(gph).GetConstraints())
                    foreach (var t in Mentions(R.GetGenericParameterConstraint(ch).Type)) AddStructural(t, "generic constraint on " + tname);
            foreach (var fh in td.GetFields())
            {
                var fd = R.GetFieldDefinition(fh);
                var col = new TypeRefCollector();
                try { fd.DecodeSignature(col, null); } catch { }
                bool isStatic = (fd.Attributes & FieldAttributes.Static) != 0;
                foreach (var t in col.Found) AddStructural(t, "field type of " + tname + "::" + R.GetString(fd.Name) + (isStatic ? " (static)" : ""));
            }
            foreach (var mh in td.GetMethods())
            {
                var md = R.GetMethodDefinition(mh);
                var col = new TypeRefCollector();
                try { md.DecodeSignature(col, null); } catch { }
                foreach (var t in col.Found) AddStructural(t, "signature of " + tname + "::" + R.GetString(md.Name));
                foreach (var gph in md.GetGenericParameters())
                    foreach (var ch in R.GetGenericParameter(gph).GetConstraints())
                        foreach (var t in Mentions(R.GetGenericParameterConstraint(ch).Type)) AddStructural(t, "generic constraint on " + tname + "::" + R.GetString(md.Name));
            }
            foreach (var ph in td.GetProperties())
            {
                var pd = R.GetPropertyDefinition(ph);
                var col = new TypeRefCollector();
                try { pd.DecodeSignature(col, null); } catch { }
                foreach (var t in col.Found) AddStructural(t, "property type of " + tname + "::" + R.GetString(pd.Name));
            }
            foreach (var mih in td.GetMethodImplementations())
            {
                var mi = R.GetMethodImplementation(mih);
                if (mi.MethodDeclaration.Kind == HandleKind.MemberReference)
                    AddMemberStructural((MemberReferenceHandle)mi.MethodDeclaration, "explicit interface/override impl (MethodImpl) in " + tname + " [TYPE-LOAD]");
                foreach (var t in Mentions(mi.MethodDeclaration)) AddStructural(t, "MethodImpl declaration in " + tname + " [TYPE-LOAD]");
            }
        }
    }

    /// <summary>TypeRefs mentioned by a token (TypeRef, TypeSpec, MemberRef, MethodSpec).</summary>
    public HashSet<TypeReferenceHandle> Mentions(EntityHandle h)
    {
        if (_mentionCache.TryGetValue(h, out var cached)) return cached;
        var set = new HashSet<TypeReferenceHandle>();
        _mentionCache[h] = set; // guard against cycles
        try
        {
            switch (h.Kind)
            {
                case HandleKind.TypeReference:
                {
                    EntityHandle cur = h;
                    while (cur.Kind == HandleKind.TypeReference)
                    {
                        set.Add((TypeReferenceHandle)cur);
                        cur = R.GetTypeReference((TypeReferenceHandle)cur).ResolutionScope;
                    }
                    break;
                }
                case HandleKind.TypeSpecification:
                {
                    var col = new TypeRefCollector();
                    R.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(col, null);
                    set.UnionWith(col.Found);
                    break;
                }
                case HandleKind.MemberReference:
                {
                    var mr = R.GetMemberReference((MemberReferenceHandle)h);
                    set.UnionWith(Mentions(mr.Parent));
                    var col = new TypeRefCollector();
                    if (mr.GetKind() == MemberReferenceKind.Method) mr.DecodeMethodSignature(col, null);
                    else mr.DecodeFieldSignature(col, null);
                    set.UnionWith(col.Found);
                    break;
                }
                case HandleKind.MethodSpecification:
                {
                    var ms = R.GetMethodSpecification((MethodSpecificationHandle)h);
                    set.UnionWith(Mentions(ms.Method));
                    var col = new TypeRefCollector();
                    ms.DecodeSignature(col, null);
                    set.UnionWith(col.Found);
                    break;
                }
            }
        }
        catch { }
        return set;
    }

    public sealed record DecodedAttr(string Owner, EntityHandle OwnerHandle, string AttrType, EntityHandle Ctor,
        CustomAttributeValue<string>? Value, string? Error);

    public string DescribeOwner(EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeDefinition: return "type " + Mod.TypeName((TypeDefinitionHandle)parent);
            case HandleKind.MethodDefinition: return "method " + MethodName((MethodDefinitionHandle)parent);
            case HandleKind.FieldDefinition:
            {
                var fd = R.GetFieldDefinition((FieldDefinitionHandle)parent);
                return "field " + Mod.TypeName(fd.GetDeclaringType()) + "::" + R.GetString(fd.Name);
            }
            case HandleKind.PropertyDefinition: return "property " + R.GetString(R.GetPropertyDefinition((PropertyDefinitionHandle)parent).Name);
            case HandleKind.Parameter: return "parameter " + R.GetString(R.GetParameter((ParameterHandle)parent).Name);
            case HandleKind.AssemblyDefinition: return "assembly";
            case HandleKind.ModuleDefinition: return "module";
            default: return parent.Kind.ToString();
        }
    }

    public string AttrTypeName(EntityHandle ctor)
    {
        EntityHandle typeH = ctor.Kind switch
        {
            HandleKind.MemberReference => R.GetMemberReference((MemberReferenceHandle)ctor).Parent,
            HandleKind.MethodDefinition => R.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
            _ => default,
        };
        return typeH.Kind switch
        {
            HandleKind.TypeReference => Names.TypeRefFullName(R, (TypeReferenceHandle)typeH),
            HandleKind.TypeDefinition => Mod.TypeName((TypeDefinitionHandle)typeH),
            _ => "?",
        };
    }

    void ScanAttributes()
    {
        foreach (var cah in R.CustomAttributes)
        {
            var ca = R.GetCustomAttribute(cah);
            var owner = DescribeOwner(ca.Parent);
            var at = AttrTypeName(ca.Constructor);
            CustomAttributeValue<string>? val = null;
            string? err = null;
            try { val = ca.DecodeValue(SigProvider.Instance); }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            Attributes.Add(new DecodedAttr(owner, ca.Parent, at, ca.Constructor, val, err));
            if (ca.Constructor.Kind == HandleKind.MemberReference)
            {
                AddMemberStructural((MemberReferenceHandle)ca.Constructor, "attribute ctor on " + owner);
                foreach (var t in Mentions(ca.Constructor)) AddStructural(t, "attribute [" + at + "] on " + owner);
            }
        }
    }

    /// <summary>All typeof(...) arguments in an attribute value (canonical names), flattened.</summary>
    public static IEnumerable<string> TypeArgs(CustomAttributeValue<string> v)
    {
        foreach (var a in v.FixedArguments) foreach (var s in TypeArgs(a)) yield return s;
        foreach (var a in v.NamedArguments) foreach (var s in TypeArgs(new CustomAttributeTypedArgument<string>(a.Type, a.Value))) yield return s;
    }

    static IEnumerable<string> TypeArgs(CustomAttributeTypedArgument<string> a)
    {
        if (a.Value is ImmutableArray<CustomAttributeTypedArgument<string>> arr)
        {
            foreach (var e in arr) foreach (var s in TypeArgs(e)) yield return s;
        }
        else if (a.Type == "System.Type" && a.Value is string s) yield return s;
    }

    /// <summary>All users of a TypeRef: IL users (any token mentioning it), locals, structural, attributes.</summary>
    public (SortedSet<string> il, SortedSet<string> structural) UsersOfType(TypeReferenceHandle t)
    {
        var il = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (tok, users) in TokenUsers)
            if (Mentions(tok).Contains(t)) il.UnionWith(users);
        foreach (var (m, set) in LocalMentions)
            if (set.Contains(t)) il.Add(m + " (local variable)");
        var st = new SortedSet<string>(StringComparer.Ordinal);
        if (StructuralUses.TryGetValue(t, out var s)) st.UnionWith(s);
        var full = Names.TypeRefFullName(R, t);
        foreach (var a in Attributes)
        {
            if (a.Value == null) continue;
            foreach (var ta in TypeArgs(a.Value.Value))
                if (ta == full || ta.Contains("<" + full) || ta.Contains("," + full) || ta.StartsWith(full + "["))
                    st.Add("typeof() in [" + a.AttrType + "] on " + a.Owner);
        }
        return (il, st);
    }

    public (SortedSet<string> il, SortedSet<string> structural) UsersOfMember(MemberReferenceHandle m)
    {
        var il = new SortedSet<string>(StringComparer.Ordinal);
        if (TokenUsers.TryGetValue(m, out var direct)) il.UnionWith(direct);
        foreach (var (tok, users) in TokenUsers)
            if (tok.Kind == HandleKind.MethodSpecification && R.GetMethodSpecification((MethodSpecificationHandle)tok).Method == m)
                il.UnionWith(users);
        var st = new SortedSet<string>(StringComparer.Ordinal);
        if (MemberStructuralUses.TryGetValue(m, out var s)) st.UnionWith(s);
        return (il, st);
    }
}

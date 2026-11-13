using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace RefCheck;

// ---------------------------------------------------------------------- abstract values

/// <summary>Abstract value on the evaluator's stack. Only what lookups need is modelled;
/// everything else is <see cref="VUnknown"/> or <see cref="VObj"/> (unknown value of a known static type).</summary>
public abstract class V { }
public sealed class VUnknown : V { public static readonly VUnknown I = new(); public override string ToString() => "?"; }
public sealed class VNull : V { public static readonly VNull I = new(); public override string ToString() => "null"; }
public sealed class VInt : V { public readonly long N; public VInt(long n) => N = n; public override string ToString() => N.ToString(); }
public sealed class VStr : V { public readonly string S; public VStr(string s) => S = s; public override string ToString() => "\"" + S + "\""; }
public sealed class VToken : V { public readonly EntityHandle H; public VToken(EntityHandle h) => H = h; }
/// <summary>A System.Type value. Approx = obtained from GetType() on a value of this static type.</summary>
public sealed class VType : V
{
    public readonly string Canon; public readonly bool Approx;
    public VType(string c, bool approx = false) { Canon = c; Approx = approx; }
    public override string ToString() => "typeof(" + Canon + ")" + (Approx ? "~" : "");
}
/// <summary>Unknown value whose static type is known (call results, args).</summary>
public sealed class VObj : V { public readonly string Type; public VObj(string t) => Type = t; public override string ToString() => "<" + Type + ">"; }
public sealed class VArray : V
{
    public readonly V?[] Items; public readonly string Elem;
    public VArray(int n, string elem) { Items = new V?[Math.Clamp(n, 0, 64)]; Elem = elem; }
    public override string ToString() => "[" + string.Join(",", Items.Select(i => i?.ToString() ?? "?")) + "]";
}
public sealed class VList : V { public readonly List<V> Items = new(); public override string ToString() => "List[" + string.Join(",", Items) + "]"; }
public sealed class VFtn : V { public readonly MethodDefinitionHandle M; public VFtn(MethodDefinitionHandle m) => M = m; }
public sealed class VDelegate : V { public readonly MethodDefinitionHandle M; public VDelegate(MethodDefinitionHandle m) => M = m; }
public sealed class VLazy : V { public readonly V Factory; public VLazy(V f) => Factory = f; }
/// <summary>Compiler iterator state machine (yield return) of a mod type.</summary>
public sealed class VIter : V { public readonly TypeDefinitionHandle T; public VIter(TypeDefinitionHandle t) => T = t; }
/// <summary>One of several values (element of a literal array read in a loop).</summary>
public sealed class VMulti : V { public readonly List<V> Options; public VMulti(IEnumerable<V> o) => Options = o.ToList(); public override string ToString() => "{" + string.Join("|", Options) + "}"; }
public sealed class VLookup : V { public readonly Lookup L; public VLookup(Lookup l) => L = l; public override string ToString() => L.Describe(); }

// ---------------------------------------------------------------------- lookups

/// <summary>A by-name reflection lookup the mod performs at runtime.</summary>
public sealed class Lookup
{
    public string Api = "";              // e.g. "AccessTools.Method", "MemberCompat.FirstMethod"
    public string? Type;                 // canonical declaring type, null = not statically known
    public bool TypeApprox;              // from GetType() on a statically typed value
    public List<string>? Names;          // in order
    public List<string>? ParamTypes;     // null = not given
    public bool ParamsUnknown;           // given but not statically known
    public string? ParamName, ParamType; // ConstructorsWithParameter / HasParameter
    public string? FieldType;            // FieldRefAccess<T,F>: F
    public Lookup? Inner;                // HasParameter: the method looked up
    public string User = "";             // Type::Method containing the call
    public int Offset;
    public bool Collected;               // flowed into List.Add: one of several optional targets

    public bool IsMemberCompat => Api.StartsWith("MemberCompat.", StringComparison.Ordinal);
    public bool IsMethodLookup => Api is "AccessTools.Method" or "AccessTools.DeclaredMethod" or "AccessTools.PropertyGetter" or "AccessTools.PropertySetter"
        or "AccessTools.DeclaredPropertyGetter" or "AccessTools.DeclaredPropertySetter" or "AccessTools.Constructor" or "AccessTools.DeclaredConstructor"
        or "AccessTools.FirstMethod" or "Type.GetMethod" or "Type.GetConstructor" or "MemberCompat.FirstMethod" or "MemberCompat.ConstructorsWithParameter";

    public string Describe()
    {
        var t = Type == null ? "<dynamic type>" : Type + (TypeApprox ? " (static type of a GetType() receiver)" : "");
        var names = Names == null ? "<names unknown>" : string.Join(", ", Names.Select(n => "\"" + n + "\""));
        var ps = ParamTypes != null ? "[" + string.Join(",", ParamTypes) + "]" : ParamsUnknown ? "[<unparsed>]" : null;
        return Api switch
        {
            "MemberCompat.FirstMethod" => $"{Api}({t}, {ps ?? "?"}, {names})",
            "MemberCompat.FirstField" => $"{Api}({t}, {names})",
            "MemberCompat.ConstructorsWithParameter" => $"{Api}({t}, \"{ParamName}\", {ParamType})",
            "MemberCompat.Construct" => $"{Api}({t}, {ps ?? "?"})",
            "MemberCompat.HasParameter" => $"{Api}({Inner?.Describe() ?? "<method>"}, \"{ParamName}\", {ParamType})",
            "AccessTools.FieldRefAccess" => $"{Api}<{t},{FieldType}>({names})",
            _ => $"{Api}({t}, {names}{(ps != null ? ", " + ps : "")})",
        };
    }
}

// ---------------------------------------------------------------------- evaluator

/// <summary>
/// A small abstract interpreter over mod IL: straight-line, branch-insensitive (branches are
/// walked in order, both arms), enough to evaluate the arguments of reflection lookups and the
/// return value of TargetMethod(s), including static readonly fields (evaluated through the
/// declaring type's .cctor), Lazy&lt;T&gt; factories (followed into the compiler's &lt;&gt;c lambdas),
/// calls into other mod methods (return value only), List&lt;T&gt;.Add, and yield-return iterators.
/// </summary>
public sealed class IlEval
{
    readonly Asm _mod;
    readonly ModIndex _idx;
    readonly HashSet<string> _gameTypeNames; // full names of mod TypeRefs scoped to the game assembly
    MetadataReader R => _mod.R;

    /// <summary>Every lookup found while evaluating, keyed by (method, offset, expansion).</summary>
    public readonly Dictionary<string, Lookup> Lookups = new(StringComparer.Ordinal);
    readonly Dictionary<TypeDefinitionHandle, Dictionary<FieldDefinitionHandle, V>> _statics = new();
    readonly HashSet<TypeDefinitionHandle> _staticsInProgress = new();
    readonly Dictionary<MethodDefinitionHandle, List<V>> _returnCache = new();
    readonly HashSet<MethodDefinitionHandle> _inProgress = new();

    public IlEval(Asm mod, ModIndex idx, string gameAsmName)
    {
        _mod = mod; _idx = idx;
        _gameTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in R.TypeReferences)
            if (string.Equals(Names.TypeRefScopeAssembly(R, h), gameAsmName, StringComparison.OrdinalIgnoreCase))
                _gameTypeNames.Add(Names.TypeRefFullName(R, h));
    }

    public bool IsGameType(string? canon)
    {
        if (canon == null) return false;
        int lt = canon.IndexOf('<');
        var bare = lt >= 0 ? canon[..lt] : canon;
        return _gameTypeNames.Contains(bare);
    }

    /// <summary>Evaluate every method body once so that every lookup is registered.</summary>
    public void EvaluateAll()
    {
        foreach (var mh in R.MethodDefinitions)
            if (_idx.Il.ContainsKey(mh)) Run(mh, 0, out _, out _);
    }

    /// <summary>All values returned (and, for iterators, yielded) by a method.</summary>
    public List<V> Returns(MethodDefinitionHandle mh, int depth)
    {
        if (_returnCache.TryGetValue(mh, out var cached)) return cached;
        if (depth > 6 || !_inProgress.Add(mh)) return new List<V> { VUnknown.I };
        try
        {
            Run(mh, depth, out var rets, out var yields);
            var all = rets.Concat(yields).ToList();
            _returnCache[mh] = all;
            return all;
        }
        finally { _inProgress.Remove(mh); }
    }

    /// <summary>Flatten a value to the lookups it denotes (arrays, lists, Lazy, iterators, delegates).</summary>
    public List<Lookup> LookupsIn(V v, int depth = 0)
    {
        var res = new List<Lookup>();
        Collect(v, res, depth, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return res;
    }

    void Collect(V v, List<Lookup> res, int depth, HashSet<object> seen)
    {
        if (depth > 8 || !seen.Add(v)) return;
        switch (v)
        {
            case VLookup l: if (!res.Contains(l.L)) res.Add(l.L); break;
            case VArray a: foreach (var i in a.Items) if (i != null) Collect(i, res, depth + 1, seen); break;
            case VList l: foreach (var i in l.Items) Collect(i, res, depth + 1, seen); break;
            case VMulti m: foreach (var i in m.Options) Collect(i, res, depth + 1, seen); break;
            case VLazy z: Collect(Invoke(z.Factory, depth + 1), res, depth + 1, seen); break;
            case VDelegate d: Collect(Invoke(d, depth + 1), res, depth + 1, seen); break;
            case VIter it:
                foreach (var m in R.GetTypeDefinition(it.T).GetMethods())
                    if (R.GetString(R.GetMethodDefinition(m).Name) == "MoveNext")
                        foreach (var y in Returns(m, depth + 1)) Collect(y, res, depth + 1, seen);
                break;
        }
    }

    V Invoke(V del, int depth)
    {
        if (del is VDelegate d)
        {
            var rets = Returns(d.M, depth + 1).Where(x => x is not VUnknown).ToList();
            return rets.Count == 0 ? VUnknown.I : rets.Count == 1 ? rets[0] : new VMulti(rets);
        }
        return VUnknown.I;
    }

    /// <summary>Static field initial values of a mod type, by evaluating its .cctor.</summary>
    V StaticValue(FieldDefinitionHandle fh, int depth)
    {
        var owner = R.GetFieldDefinition(fh).GetDeclaringType();
        if (!_statics.TryGetValue(owner, out var map))
        {
            if (!_staticsInProgress.Add(owner)) return VUnknown.I;
            map = new Dictionary<FieldDefinitionHandle, V>();
            try
            {
                foreach (var m in R.GetTypeDefinition(owner).GetMethods())
                    if (R.GetString(R.GetMethodDefinition(m).Name) == ".cctor")
                        Run(m, depth + 1, out _, out _, map);
            }
            finally { _staticsInProgress.Remove(owner); }
            _statics[owner] = map;
        }
        return map.TryGetValue(fh, out var v) ? v : VUnknown.I;
    }

    // ------------------------------------------------------------------ call-site description

    sealed record Callee(string Owner, string Name, int NParams, bool HasThis, string Ret, ImmutableArray<string> GenArgs, MethodDefinitionHandle Def);

    Callee? Describe(EntityHandle tok)
    {
        var gen = ImmutableArray<string>.Empty;
        if (tok.Kind == HandleKind.MethodSpecification)
        {
            var ms = R.GetMethodSpecification((MethodSpecificationHandle)tok);
            try { gen = ms.DecodeSignature(SigProvider.Instance, null); } catch { }
            tok = ms.Method;
        }
        try
        {
            if (tok.Kind == HandleKind.MethodDefinition)
            {
                var md = R.GetMethodDefinition((MethodDefinitionHandle)tok);
                var sig = md.DecodeSignature(SigProvider.Instance, null);
                return new Callee(_mod.TypeName(md.GetDeclaringType()), R.GetString(md.Name), sig.ParameterTypes.Length, sig.Header.IsInstance, sig.ReturnType, gen, (MethodDefinitionHandle)tok);
            }
            if (tok.Kind == HandleKind.MemberReference)
            {
                var mr = R.GetMemberReference((MemberReferenceHandle)tok);
                if (mr.GetKind() != MemberReferenceKind.Method) return null;
                var sig = mr.DecodeMethodSignature(SigProvider.Instance, null);
                string owner = mr.Parent.Kind switch
                {
                    HandleKind.TypeReference => Names.TypeRefFullName(R, (TypeReferenceHandle)mr.Parent),
                    HandleKind.TypeDefinition => _mod.TypeName((TypeDefinitionHandle)mr.Parent),
                    HandleKind.TypeSpecification => Resolver.TryGenericInst(R, (TypeSpecificationHandle)mr.Parent, null, out var und, out _)
                        ? (und.Kind == HandleKind.TypeReference ? Names.TypeRefFullName(R, (TypeReferenceHandle)und) : und.Kind == HandleKind.TypeDefinition ? _mod.TypeName((TypeDefinitionHandle)und) : "?")
                        : R.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).DecodeSignature(SigProvider.Instance, null),
                    _ => "?",
                };
                return new Callee(owner, R.GetString(mr.Name), sig.ParameterTypes.Length, sig.Header.IsInstance, sig.ReturnType, gen, default);
            }
        }
        catch { }
        return null;
    }

    string TokenTypeName(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeReference => Names.TypeRefFullName(R, (TypeReferenceHandle)h),
        HandleKind.TypeDefinition => _mod.TypeName((TypeDefinitionHandle)h),
        HandleKind.TypeSpecification => R.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(SigProvider.Instance, null),
        _ => "?",
    };

    // ------------------------------------------------------------------ interpreter

    sealed class Frame
    {
        public readonly List<V> Stack = new();
        public readonly Dictionary<long, V> Locals = new();
        public void Push(V v) => Stack.Add(v);
        public V Pop() { if (Stack.Count == 0) return VUnknown.I; var v = Stack[^1]; Stack.RemoveAt(Stack.Count - 1); return v; }
        public V Peek() => Stack.Count == 0 ? VUnknown.I : Stack[^1];
        public void Pop(int n) { for (int i = 0; i < n; i++) Pop(); }
    }

    void Run(MethodDefinitionHandle mh, int depth, out List<V> rets, out List<V> yields, Dictionary<FieldDefinitionHandle, V>? statics = null)
    {
        rets = new List<V>(); yields = new List<V>();
        if (!_idx.Il.TryGetValue(mh, out var il)) return;
        var user = _idx.MethodName(mh);
        var f = new Frame();
        foreach (var ins in il)
        {
            ushort op = ins.Op;
            switch (op)
            {
                case 0x00: case 0xFE12: case 0xFE13: case 0xFE14: case 0xFE16: case 0xFE1E: break; // nop / prefixes
                case >= 0x02 and <= 0x05: f.Push(VUnknown.I); break;                 // ldarg.0-3
                case 0x0E: case 0x0F: case 0xFE09: case 0xFE0A: f.Push(VUnknown.I); break; // ldarg(.s) / ldarga(.s)
                case 0x10: case 0xFE0B: f.Pop(); break;                                  // starg
                case >= 0x06 and <= 0x09: f.Push(f.Locals.TryGetValue(op - 0x06, out var l0) ? l0 : VUnknown.I); break;
                case 0x11: case 0xFE0C: f.Push(f.Locals.TryGetValue(ins.Num, out var l1) ? l1 : VUnknown.I); break;
                case 0x12: case 0xFE0D: f.Push(VUnknown.I); break;                       // ldloca
                case >= 0x0A and <= 0x0D: f.Locals[op - 0x0A] = f.Pop(); break;
                case 0x13: case 0xFE0E: f.Locals[ins.Num] = f.Pop(); break;
                case 0x14: f.Push(VNull.I); break;
                case 0x15: f.Push(new VInt(-1)); break;
                case >= 0x16 and <= 0x1E: f.Push(new VInt(op - 0x16)); break;
                case 0x1F: case 0x20: f.Push(new VInt(ins.Num)); break;
                case 0x21: case 0x22: case 0x23: f.Push(VUnknown.I); break;
                case 0x25: f.Push(f.Peek()); break;                                       // dup
                case 0x26: f.Pop(); break;                                                // pop
                case 0x28: case 0x6F: case 0x73: Call(f, ins, op == 0x73, depth, user); break;
                case 0x29: f.Stack.Clear(); break;                                        // calli
                case 0x2A: if (f.Stack.Count > 0) rets.Add(f.Pop()); f.Stack.Clear(); break; // ret
                case 0x2B: case 0x38: break;                                              // br
                case 0x2C: case 0x2D: case 0x39: case 0x3A: case 0x45: f.Pop(); break;    // brfalse/brtrue/switch
                case >= 0x2E and <= 0x37: case >= 0x3B and <= 0x44: f.Pop(2); break;      // compare-branch
                case >= 0x46 and <= 0x50: f.Pop(); f.Push(VUnknown.I); break;             // ldind
                case >= 0x51 and <= 0x57: case 0xDF: f.Pop(2); break;                     // stind
                case >= 0x58 and <= 0x64: case >= 0xD6 and <= 0xDB: case 0xFE01: case 0xFE02: case 0xFE03: case 0xFE04: case 0xFE05:
                    f.Pop(2); f.Push(VUnknown.I); break;                                  // binary ops
                case 0x65: case 0x66: case >= 0x67 and <= 0x6E: case 0x76: case >= 0x82 and <= 0x8B: case >= 0xB3 and <= 0xBA:
                case 0xC3: case >= 0xD1 and <= 0xD5: case 0xE0: case 0x79: case 0x71: case 0x8E: case 0xA5: case 0xFE07: case 0xFE0F: case 0xC2: case 0xFE1D:
                    f.Pop(); f.Push(VUnknown.I); break;                                   // unary / conversions
                case 0x74: case 0x75: case 0x8C: break;                                   // castclass / isinst / box: keep value
                case 0x70: case 0x81: f.Pop(2); break;                                    // cpobj / stobj
                case 0x72: f.Push(new VStr(ins.Str ?? "")); break;                        // ldstr
                case 0x7A: case 0xDC: case 0xDD: case 0xDE: case 0xFE1A: case 0xFE11: f.Stack.Clear(); break; // throw/endfinally/leave/rethrow/endfilter
                case 0x7B: case 0x7C: f.Pop(); f.Push(VUnknown.I); break;                 // ldfld / ldflda
                case 0x7D:                                                                 // stfld
                {
                    var val = f.Pop(); f.Pop();
                    if (ins.Token.Kind == HandleKind.FieldDefinition && R.GetString(R.GetFieldDefinition((FieldDefinitionHandle)ins.Token).Name) == "<>2__current")
                        yields.Add(val);
                    break;
                }
                case 0x7E: f.Push(LoadStatic(ins.Token, depth)); break;                   // ldsfld
                case 0x7F: f.Push(VUnknown.I); break;                                     // ldsflda
                case 0x80:                                                                 // stsfld
                {
                    var val = f.Pop();
                    if (statics != null && ins.Token.Kind == HandleKind.FieldDefinition) statics[(FieldDefinitionHandle)ins.Token] = val;
                    break;
                }
                case 0x8D:                                                                 // newarr
                {
                    var n = f.Pop();
                    f.Push(new VArray(n is VInt i ? (int)i.N : 0, TokenTypeName(ins.Token)));
                    break;
                }
                case 0x8F: f.Pop(2); f.Push(VUnknown.I); break;                           // ldelema
                case >= 0x90 and <= 0x9A: case 0xA3:                                       // ldelem
                {
                    f.Pop(); var arr = f.Pop();
                    // A literal array indexed by a (loop) variable: any of its elements.
                    if (arr is VArray a && a.Items.Length > 0 && a.Items.All(x => x != null))
                        f.Push(a.Items.Length == 1 ? a.Items[0]! : new VMulti(a.Items!));
                    else f.Push(VUnknown.I);
                    break;
                }
                case >= 0x9B and <= 0xA2: case 0xA4:                                       // stelem
                {
                    var val = f.Pop(); var idx = f.Pop(); var arr = f.Pop();
                    if (arr is VArray a && idx is VInt i && i.N >= 0 && i.N < a.Items.Length) a.Items[i.N] = val;
                    break;
                }
                case 0xD0: f.Push(new VToken(ins.Token)); break;                          // ldtoken
                case 0xFE06:                                                               // ldftn
                    f.Push(ins.Token.Kind == HandleKind.MethodDefinition ? new VFtn((MethodDefinitionHandle)ins.Token) : VUnknown.I); break;
                case 0xFE15: f.Pop(); break;                                              // initobj
                case 0xFE17: case 0xFE18: f.Pop(3); break;                                // cpblk / initblk
                case 0xFE1C: f.Push(VUnknown.I); break;                                   // sizeof
                default: f.Stack.Clear(); break;                                           // unknown: resynchronise
            }
        }
    }

    V LoadStatic(EntityHandle tok, int depth)
    {
        if (tok.Kind == HandleKind.FieldDefinition) return StaticValue((FieldDefinitionHandle)tok, depth);
        if (tok.Kind == HandleKind.MemberReference)
        {
            var mr = R.GetMemberReference((MemberReferenceHandle)tok);
            if (mr.Parent.Kind == HandleKind.TypeReference && Names.TypeRefFullName(R, (TypeReferenceHandle)mr.Parent) == "System.Type" && R.GetString(mr.Name) == "EmptyTypes")
                return new VArray(0, "System.Type");
        }
        return VUnknown.I;
    }

    void Call(Frame f, Instr ins, bool isNewobj, int depth, string user)
    {
        var c = Describe(ins.Token);
        if (c == null) { f.Stack.Clear(); return; }
        var args = new V[c.NParams];
        for (int i = c.NParams - 1; i >= 0; i--) args[i] = f.Pop();
        V self = VUnknown.I;
        if (c.HasThis && !isNewobj) self = f.Pop();
        bool returns = isNewobj || c.Ret != "System.Void";
        var result = Interpret(c, self, args, isNewobj, depth, user, ins.Offset);
        if (returns) f.Push(result ?? (isNewobj ? new VObj(c.Owner) : new VObj(c.Ret)));
    }

    V? Interpret(Callee c, V self, V[] a, bool isNewobj, int depth, string user, int offset)
    {
        string o = c.Owner, n = c.Name;
        if (o == "System.Type" && n == "GetTypeFromHandle" && a.Length == 1 && a[0] is VToken t)
            return new VType(TokenTypeName(t.H));
        if (o == "System.Object" && n == "GetType" && self is VObj so && so.Type != "System.Object")
            return new VType(so.Type, approx: true);
        if (o == "System.Array" && n == "Empty") return new VArray(0, c.GenArgs.FirstOrDefault() ?? "?");
        if (o == "System.Lazy`1")
        {
            if (isNewobj && n == ".ctor" && a.Length >= 1 && a[0] is VDelegate) return new VLazy(a[0]);
            if (n == "get_Value" && self is VLazy z) return Invoke(z.Factory, depth);
        }
        if (o.StartsWith("System.Func`", StringComparison.Ordinal))
        {
            if (isNewobj && a.Length == 2 && a[1] is VFtn ftn) return new VDelegate(ftn.M);
            if (n == "Invoke" && self is VDelegate) return Invoke(self, depth);
        }
        if (o == "System.Collections.Generic.List`1")
        {
            if (isNewobj)
            {
                var l = new VList();
                if (a.Length == 1 && a[0] is VArray arr) l.Items.AddRange(arr.Items.Where(x => x != null)!);
                if (a.Length == 1 && a[0] is VList src) l.Items.AddRange(src.Items);
                return l;
            }
            if ((n == "Add" || n == "AddRange") && self is VList list && a.Length == 1)
            {
                // Method lookups added to a list are individually optional Harmony targets
                // (TargetMethods code filters nulls before Add). Only method lookups: the
                // RESULT of e.g. Construct() flowing into a list says nothing about optionality.
                foreach (var lk in LookupsIn(a[0], depth)) if (lk.IsMethodLookup) lk.Collected = true;
                if (n == "Add") list.Items.Add(a[0]); else list.Items.Add(a[0]);
                return null;
            }
        }
        if (o == "System.Linq.Enumerable" && n is "ToList" or "ToArray" or "AsEnumerable" && a.Length == 1) return a[0];
        if (isNewobj && c.Def.IsNil == false && o.Contains(">d__", StringComparison.Ordinal))
            return new VIter(R.GetMethodDefinition(c.Def).GetDeclaringType());

        var lookup = LookupCall(c, self, a, depth, user, offset);
        if (lookup != null) return lookup;

        // Other mod methods: their return value (arguments ignored).
        if (!c.Def.IsNil && !isNewobj && c.Ret != "System.Void")
        {
            var rets = Returns(c.Def, depth + 1).Where(x => x is not VUnknown && x is not VObj).ToList();
            if (rets.Count == 1) return rets[0];
            if (rets.Count > 1) return new VMulti(rets);
        }
        return null;
    }

    // ------------------------------------------------------------------ lookup recognition

    static readonly HashSet<string> AccessToolsApis = new(StringComparer.Ordinal)
    {
        "Method", "DeclaredMethod", "Field", "DeclaredField", "Property", "DeclaredProperty", "PropertyGetter", "PropertySetter",
        "DeclaredPropertyGetter", "DeclaredPropertySetter", "Constructor", "DeclaredConstructor", "FirstMethod", "FieldRefAccess", "StaticFieldRefAccess",
    };

    V? LookupCall(Callee c, V self, V[] a, int depth, string user, int offset)
    {
        string o = c.Owner, n = c.Name;
        bool memberCompat = o.EndsWith(".MemberCompat", StringComparison.Ordinal) && !c.Def.IsNil
            && n is "FirstMethod" or "FirstField" or "ConstructorsWithParameter" or "Construct" or "HasParameter";
        bool accessTools = o == "HarmonyLib.AccessTools" && AccessToolsApis.Contains(n);
        bool typeApi = o == "System.Type" && n is "GetMethod" or "GetField" or "GetProperty" or "GetConstructor";
        if (!memberCompat && !accessTools && !typeApi) return null;

        // Expand one-of values (e.g. a foreach over a literal name array) into separate lookups.
        var combos = Expand(a);
        var results = new List<V>();
        for (int k = 0; k < combos.Count; k++)
        {
            var args = combos[k];
            var lk = new Lookup { User = user, Offset = offset };
            if (memberCompat) BuildMemberCompat(lk, n, args, depth);
            else if (accessTools) BuildAccessTools(lk, n, args, c.GenArgs);
            else BuildTypeApi(lk, n, self, args);
            var key = user + "@" + offset + "#" + lk.Describe();
            if (Lookups.TryGetValue(key, out var existing)) lk = existing; else Lookups[key] = lk;
            results.Add(new VLookup(lk));
        }
        return results.Count == 1 ? results[0] : new VMulti(results);
    }

    static List<V[]> Expand(V[] a)
    {
        var res = new List<V[]> { a };
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is not VMulti m) continue;
            var next = new List<V[]>();
            foreach (var r in res)
                foreach (var opt in m.Options.Take(8)) { var c = (V[])r.Clone(); c[i] = opt; next.Add(c); }
            res = next;
            if (res.Count > 32) break;
        }
        // nested: an array element that is itself one-of (params string[] { name } inside a loop)
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] is not VArray arr) continue;
            for (int j = 0; j < arr.Items.Length; j++)
            {
                if (arr.Items[j] is not VMulti m) continue;
                var next = new List<V[]>();
                foreach (var r in res)
                    foreach (var opt in m.Options.Take(8))
                    {
                        var c = (V[])r.Clone();
                        var src = (VArray)c[i];
                        var copy = new VArray(src.Items.Length, src.Elem);
                        Array.Copy(src.Items, copy.Items, src.Items.Length);
                        copy.Items[j] = opt;
                        c[i] = copy;
                        next.Add(c);
                    }
                res = next;
            }
        }
        return res;
    }

    static string? AsType(V v, out bool approx)
    {
        approx = false;
        if (v is VType t) { approx = t.Approx; return t.Canon; }
        return null;
    }

    static List<string>? AsStrings(V v) => v switch
    {
        VStr s => new List<string> { s.S },
        VArray arr when arr.Items.All(i => i is VStr) => arr.Items.Select(i => ((VStr)i!).S).ToList(),
        _ => null,
    };

    /// <summary>Type[] argument: (types, unknown). null types + !unknown = not given.</summary>
    static (List<string>? types, bool unknown) AsTypes(V v) => v switch
    {
        VNull => (null, false),
        VArray arr when arr.Items.All(i => i is VType) => (arr.Items.Select(i => ((VType)i!).Canon).ToList(), false),
        _ => (null, true),
    };

    void BuildMemberCompat(Lookup lk, string n, V[] a, int depth)
    {
        lk.Api = "MemberCompat." + n;
        switch (n)
        {
            case "FirstMethod": // (Type, Type[], params string[])
                lk.Type = AsType(a[0], out lk.TypeApprox);
                (lk.ParamTypes, lk.ParamsUnknown) = AsTypes(a[1]);
                lk.Names = AsStrings(a[2]);
                break;
            case "FirstField": // (Type, params string[])
                lk.Type = AsType(a[0], out lk.TypeApprox);
                lk.Names = AsStrings(a[1]);
                break;
            case "ConstructorsWithParameter": // (Type, string, Type)
                lk.Type = AsType(a[0], out lk.TypeApprox);
                lk.ParamName = (a[1] as VStr)?.S;
                lk.ParamType = (a[2] as VType)?.Canon;
                lk.Names = new List<string> { ".ctor" };
                break;
            case "Construct": // (Type, Type[], object[])
                lk.Type = AsType(a[0], out lk.TypeApprox);
                (lk.ParamTypes, lk.ParamsUnknown) = AsTypes(a[1]);
                lk.Names = new List<string> { ".ctor" };
                break;
            case "HasParameter": // (MethodBase, string, Type)
                lk.Inner = LookupsIn(a[0], depth).FirstOrDefault();
                lk.ParamName = (a[1] as VStr)?.S;
                lk.ParamType = (a[2] as VType)?.Canon;
                lk.Type = lk.Inner?.Type;
                lk.Names = lk.Inner?.Names;
                break;
        }
    }

    void BuildAccessTools(Lookup lk, string n, V[] a, ImmutableArray<string> gen)
    {
        lk.Api = "AccessTools." + n;
        if (n is "FieldRefAccess" or "StaticFieldRefAccess")
        {
            // FieldRefAccess<T,F>(string) | FieldRefAccess<F>(Type, string) | StaticFieldRefAccess<T,F>(string) | <F>(Type, string)
            if (gen.Length == 2 && a.Length == 1 && a[0] is VStr s1) { lk.Type = gen[0]; lk.FieldType = gen[1]; lk.Names = new() { s1.S }; }
            else if (gen.Length == 1 && a.Length == 2 && a[1] is VStr s2) { lk.Type = AsType(a[0], out lk.TypeApprox); lk.FieldType = gen[0]; lk.Names = new() { s2.S }; }
            else { lk.Type = gen.Length > 0 ? gen[0] : null; lk.Names = null; }
            return;
        }
        lk.Type = a.Length > 0 ? AsType(a[0], out lk.TypeApprox) : null;
        if (n is "Constructor" or "DeclaredConstructor")
        {
            lk.Names = new() { ".ctor" };
            if (a.Length > 1) (lk.ParamTypes, lk.ParamsUnknown) = AsTypes(a[1]);
            else lk.ParamTypes = null;
            return;
        }
        lk.Names = a.Length > 1 && a[1] is VStr s ? new() { s.S } : null;
        if (n is "Method" or "DeclaredMethod" && a.Length > 2) (lk.ParamTypes, lk.ParamsUnknown) = AsTypes(a[2]);
    }

    void BuildTypeApi(Lookup lk, string n, V self, V[] a)
    {
        lk.Api = "Type." + n;
        lk.Type = AsType(self, out lk.TypeApprox);
        if (n == "GetConstructor") { lk.Names = new() { ".ctor" }; lk.ParamsUnknown = true; return; }
        lk.Names = a.Length > 0 && a[0] is VStr s ? new() { s.S } : null;
        // GetMethod(name, Type[]) / (name, flags, binder, Type[], mods): a Type[] argument is the signature
        var typesArg = a.Skip(1).FirstOrDefault(x => x is VArray va && (va.Elem == "System.Type" || va.Items.All(i => i is VType)));
        if (n == "GetMethod" && typesArg != null) (lk.ParamTypes, lk.ParamsUnknown) = AsTypes(typesArg);
        else if (n == "GetMethod" && a.Length > 1) lk.ParamsUnknown = true;
    }
}

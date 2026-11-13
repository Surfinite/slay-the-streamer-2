using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace RefCheck;

/// <summary>
/// Checks everything the mod binds BY NAME at runtime (no compiled reference exists):
///  - Harmony patch targets: [HarmonyPatch(typeof(X), "M", ...)] attributes (decoded from the
///    attribute blobs) and TargetMethod()/TargetMethods() return values, evaluated by
///    <see cref="IlEval"/> (follows static readonly Lazy fields into their &lt;&gt;c lambdas, mod
///    helper getters, List.Add, yield return);
///  - Prefix/Postfix/Finalizer parameter binding on each resolved target (Harmony throws during
///    PatchAll when a name does not bind, which disables every later patch class);
///  - every reflection lookup anywhere in the mod: MemberCompat.FirstMethod / FirstField /
///    ConstructorsWithParameter / Construct / HasParameter, AccessTools.Method/Field/Property/...,
///    AccessTools.FieldRefAccess&lt;T,F&gt;, Type.GetMethod/GetField/GetProperty.
/// Severity policy (see README): FAIL = breaks unconditionally; GATED = a Harmony class with
/// Prepare() that will skip itself on this version; OPTIONAL = a lookup the code tolerates
/// missing; INFO = shape gates (HasParameter) and dynamic lookups.
/// </summary>
public sealed class HarmonyCheck
{
    readonly Asm _mod;
    readonly AsmSet _set;
    readonly ModIndex _idx;
    readonly TextWriter _o;
    readonly List<string> _dump;
    readonly IlEval _eval;
    MetadataReader MR => _mod.R;
    Asm G => _set.Game;

    public string Summary = "";
    public int Failures, Gated, Undecided, Optional, TargetsOk;
    readonly List<string> _failLines = new(), _gatedLines = new(), _optionalLines = new(), _undecidedLines = new();
    readonly HashSet<Lookup> _targetLookups = new();
    readonly Dictionary<Lookup, string> _targetOwner = new();

    static readonly HashSet<string> SpecialParams = new(StringComparer.Ordinal)
    { "__instance", "__result", "__resultRef", "__state", "__originalMethod", "__args", "__runOriginal", "__exception" };

    static readonly HashSet<string> PatchMethodNames = new(StringComparer.Ordinal) { "Prefix", "Postfix", "Finalizer", "Transpiler", "ILManipulator" };
    static readonly Dictionary<string, string> PatchAttrToKind = new(StringComparer.Ordinal)
    {
        ["HarmonyLib.HarmonyPrefix"] = "Prefix", ["HarmonyLib.HarmonyPostfix"] = "Postfix",
        ["HarmonyLib.HarmonyFinalizer"] = "Finalizer", ["HarmonyLib.HarmonyTranspiler"] = "Transpiler",
    };

    public HarmonyCheck(Asm mod, AsmSet set, ModIndex idx, TextWriter o, List<string> dump)
    {
        _mod = mod; _set = set; _idx = idx; _o = o; _dump = dump;
        _eval = new IlEval(mod, idx, set.Game.Name);
        _eval.EvaluateAll();
    }

    // ------------------------------------------------------------------ attribute decoding

    sealed class PatchInfo
    {
        public string? Type;
        public string? Method;
        public int MethodType;        // HarmonyLib.MethodType: 0 Normal,1 Getter,2 Setter,3 Constructor,4 StaticConstructor,5 Enumerator,6 Async
        public string[]? ArgTypes;
        public int[]? ArgVariations;  // ArgumentType: 0 Normal,1 Ref,2 Out,3 Pointer
        public bool Any => Type != null || Method != null || ArgTypes != null || MethodType != 0;

        public PatchInfo Merge(PatchInfo over) => new()
        {
            Type = over.Type ?? Type,
            Method = over.Method ?? Method,
            MethodType = over.MethodType != 0 ? over.MethodType : MethodType,
            ArgTypes = over.ArgTypes ?? ArgTypes,
            ArgVariations = over.ArgVariations ?? ArgVariations,
        };
    }

    static string MethodTypeName(int t) => t switch { 0 => "Normal", 1 => "Getter", 2 => "Setter", 3 => "Constructor", 4 => "StaticConstructor", 5 => "Enumerator", 6 => "Async", _ => "MethodType" + t };

    static PatchInfo FromAttr(CustomAttributeValue<string> v)
    {
        var p = new PatchInfo();
        var strings = new List<string>();
        foreach (var a in v.FixedArguments)
        {
            if (a.Type == "System.Type" && a.Value is string ts) { p.Type ??= ts; continue; }
            if (a.Type == "System.String" && a.Value is string s) { strings.Add(s); continue; }
            if (a.Type == "HarmonyLib.MethodType" && a.Value is int mt) { p.MethodType = mt; continue; }
            if (a.Value is ImmutableArray<CustomAttributeTypedArgument<string>> arr)
            {
                if (a.Type == "System.Type[]") p.ArgTypes = arr.Select(e => e.Value as string ?? "?null").ToArray();
                else if (a.Type == "HarmonyLib.ArgumentType[]") p.ArgVariations = arr.Select(e => e.Value is int i ? i : 0).ToArray();
            }
        }
        if (strings.Count == 1) p.Method = strings[0];
        else if (strings.Count >= 2) { p.Type ??= strings[0]; p.Method = strings[1]; }
        return p;
    }

    static bool SameInfo(PatchInfo a, PatchInfo b) => a.Type == b.Type && a.Method == b.Method && a.MethodType == b.MethodType
        && (a.ArgTypes == null ? b.ArgTypes == null : b.ArgTypes != null && a.ArgTypes.SequenceEqual(b.ArgTypes));

    static string[] ArgCanon(PatchInfo p)
    {
        if (p.ArgTypes == null) return Array.Empty<string>();
        var r = new string[p.ArgTypes.Length];
        for (int i = 0; i < r.Length; i++)
        {
            var t = p.ArgTypes[i];
            int v = p.ArgVariations != null && i < p.ArgVariations.Length ? p.ArgVariations[i] : 0;
            r[i] = v is 1 or 2 ? t + "&" : v == 3 ? t + "*" : t;
        }
        return r;
    }

    /// <summary>Split "A,B&lt;C,D&gt;,E" at top-level commas.</summary>
    public static List<string> SplitTopLevel(string s)
    {
        var r = new List<string>();
        if (s.Length == 0) return r;
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '<' || s[i] == '[' || s[i] == '(') depth++;
            else if (s[i] == '>' || s[i] == ']' || s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) { r.Add(s[start..i]); start = i + 1; }
        }
        r.Add(s[start..]);
        return r;
    }

    // ------------------------------------------------------------------ main

    sealed class Target
    {
        public string Desc = "";
        public string Status = "";            // OK, OK-NAME..., TARGET MISSING, ...
        public bool Ok;
        public List<MethodDefinitionHandle> Resolved = new();
        public Lookup? Lookup;                // null for attribute targets
        public List<string> Notes = new();
        public List<(MethodDefinitionHandle mh, string kind)> Pms = new();
        public string? DumpMember, DumpPc, DumpPs;
    }

    public void Run()
    {
        _o.WriteLine("=== HARMONY PATCH TARGETS (bound by name at PatchAll time) ===");
        var attrsByOwner = _idx.Attributes.Where(a => a.AttrType == "HarmonyLib.HarmonyPatch").GroupBy(a => a.OwnerHandle).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var e in _idx.Attributes.Where(a => a.AttrType == "HarmonyLib.HarmonyPatch" && a.Value == null))
            Undecide($"could not decode [HarmonyPatch] on {e.Owner}: {e.Error}");

        foreach (var th in MR.TypeDefinitions)
        {
            var td = MR.GetTypeDefinition(th);
            var methods = td.GetMethods().Select(mh => (mh, name: MR.GetString(MR.GetMethodDefinition(mh).Name))).ToList();
            var patchMethods = new List<(MethodDefinitionHandle mh, string kind)>();
            bool hasPrepare = false;
            foreach (var (mh, name) in methods)
            {
                string? kind = PatchMethodNames.Contains(name) ? name : null;
                foreach (var a in _idx.Attributes.Where(a => a.OwnerHandle == (EntityHandle)mh))
                {
                    if (PatchAttrToKind.TryGetValue(a.AttrType, out var k)) kind = k;
                    if (a.AttrType == "HarmonyLib.HarmonyPrepare") hasPrepare = true;
                }
                if (name == "Prepare") hasPrepare = true;
                if (kind != null) patchMethods.Add((mh, kind));
            }
            bool classHasAttr = attrsByOwner.ContainsKey(th);
            if (!classHasAttr && !methods.Any(m => attrsByOwner.ContainsKey(m.mh))) continue;

            var tname = _mod.TypeName(th);
            var targetMethodDefs = methods.Where(m => m.name is "TargetMethod" or "TargetMethods").Select(m => m.mh).ToList();
            var classInfo = new PatchInfo();
            if (attrsByOwner.TryGetValue(th, out var classAttrs))
                foreach (var a in classAttrs) if (a.Value != null) classInfo = classInfo.Merge(FromAttr(a.Value.Value));

            var targets = new List<Target>();
            if (targetMethodDefs.Count > 0)
            {
                var returned = targetMethodDefs.SelectMany(tm => _eval.Returns(tm, 0)).ToList();
                var lookups = returned.SelectMany(v => _eval.LookupsIn(v)).Distinct().ToList();
                if (lookups.Count == 0)
                {
                    var what = string.Join(", ", returned.Select(v => v.ToString()).Distinct());
                    targets.Add(new Target
                    {
                        Desc = $"TargetMethod(s) of {tname}",
                        Status = "UNDECIDED",
                        Notes = { "return value is not statically evaluable: returns " + (what.Length == 0 ? "nothing" : what) },
                        Pms = patchMethods,
                    });
                }
                foreach (var lk in lookups)
                {
                    _targetLookups.Add(lk);
                    // a shared lookup (e.g. CardRewardCtors.WithPlayer) can target several patch classes
                    _targetOwner[lk] = _targetOwner.TryGetValue(lk, out var prev) ? prev + ", " + tname : tname;
                    targets.Add(TargetFromLookup(lk, patchMethods));
                }
            }
            else
            {
                var groups = new List<(PatchInfo info, List<(MethodDefinitionHandle mh, string kind)> pms)>();
                var shared = patchMethods.Where(p => !attrsByOwner.ContainsKey(p.mh)).ToList();
                if (shared.Count > 0 || (patchMethods.Count == 0 && classInfo.Any)) groups.Add((classInfo, shared));
                foreach (var pm in patchMethods.Where(p => attrsByOwner.ContainsKey(p.mh)))
                {
                    var info = classInfo;
                    foreach (var a in attrsByOwner[pm.mh]) if (a.Value != null) info = info.Merge(FromAttr(a.Value.Value));
                    var g = groups.FindIndex(x => SameInfo(x.info, info));
                    if (g >= 0) groups[g].pms.Add(pm); else groups.Add((info, new() { pm }));
                }
                foreach (var (info, pms) in groups)
                {
                    if (!info.Any && pms.Count == 0) continue;
                    var argDesc = info.ArgTypes == null ? "" : "(" + string.Join(",", ArgCanon(info)) + ")";
                    var desc = $"{info.Type}::{info.Method ?? "<" + MethodTypeName(info.MethodType) + ">"}{argDesc}" + (info.MethodType != 0 ? " [" + MethodTypeName(info.MethodType) + "]" : "");
                    var t = new Target { Desc = desc, Pms = pms };
                    if (info.Type == null) { t.Status = "UNDECIDED"; t.Notes.Add("attribute names no declaring type"); targets.Add(t); continue; }
                    if (!G.Types.ContainsKey(info.Type) && !_eval.IsGameType(info.Type))
                    {
                        // e.g. a Godot or BCL method: outside the game assembly, not checked here
                        t.Status = "OK-NOT-CHECKED (not a game type)"; t.Ok = true; targets.Add(t); continue;
                    }
                    string api = info.MethodType switch { 1 => "DeclaredPropertyGetter", 2 => "DeclaredPropertySetter", 3 => "DeclaredConstructor", 4 => "StaticConstructor", _ => "DeclaredMethod" };
                    var (st, res) = ResolveNamed(info.Type, info.Method, api, info.ArgTypes == null ? null : ArgCanon(info), declaredOnly: true);
                    t.Status = st; t.Ok = st.StartsWith("OK"); t.Resolved = res;
                    t.DumpMember = info.MethodType switch { 1 => "get_" + info.Method, 2 => "set_" + info.Method, 3 => ".ctor", _ => info.Method };
                    if (info.ArgTypes != null) { t.DumpPc = info.ArgTypes.Length.ToString(); t.DumpPs = string.Join(";", ArgCanon(info)); }
                    targets.Add(t);
                }
            }

            _o.WriteLine();
            _o.WriteLine($"{tname}{(hasPrepare ? "  [has Prepare(): can skip itself]" : "")}");
            foreach (var t in targets)
            {
                _o.WriteLine($"    target {t.Status}: {t.Desc}");
                foreach (var n in t.Notes) _o.WriteLine($"        {n}");
                foreach (var r in t.Resolved) _o.WriteLine($"        -> {DescribeGameMethod(r)}");
                Classify(t, tname, hasPrepare);
                AddDump(t, tname, hasPrepare);
            }
        }
        _o.WriteLine();

        ReportLookups();

        _o.WriteLine($"=== HARMONY/LOOKUP FAILURES ({_failLines.Count}) ===");
        foreach (var l in _failLines) _o.WriteLine("  " + l);
        _o.WriteLine();
        _o.WriteLine($"=== GATED (class has Prepare; expected to skip itself on this game version) ({_gatedLines.Count}) ===");
        foreach (var l in _gatedLines) _o.WriteLine("  " + l);
        _o.WriteLine();
        _o.WriteLine($"=== OPTIONAL LOOKUPS NOT FOUND (informational) ({_optionalLines.Count}) ===");
        foreach (var l in _optionalLines) _o.WriteLine("  " + l);
        _o.WriteLine();
        _o.WriteLine($"=== UNDECIDED ({_undecidedLines.Count}) ===");
        foreach (var l in _undecidedLines) _o.WriteLine("  " + l);
        _o.WriteLine();
        Failures = _failLines.Count; Gated = _gatedLines.Count; Optional = _optionalLines.Count; Undecided = _undecidedLines.Count;
        Summary = $"{TargetsOk} target(s) OK, {Failures} FAIL, {Gated} GATED, {Undecided} UNDECIDED, {Optional} optional lookup(s) not found";
    }

    void Undecide(string line) => _undecidedLines.Add(line);

    readonly List<string> _resolutionLines = new();
    /// <summary>One compact line per MemberCompat / FieldRefAccess lookup, for run.ps1.</summary>
    public IReadOnlyList<string> ResolutionLines => _resolutionLines;

    /// <summary>Drop namespaces and "instance"/"static" markers for one-line display.</summary>
    static string Short(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?:[A-Za-z_][\w`]*\.)+([A-Za-z_][\w`]*)", "$1");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\b(instance|static) ", "");
    }

    static string OwnerShort(string user)
    {
        var t = user.Split("::")[0];
        var segs = t.Split('/').Where(x => !x.StartsWith("<")).ToList();
        if (segs.Count == 0) return t;
        segs[0] = segs[0].Split('.').Last();
        return string.Join("/", segs);
    }

    /// <summary>Lookups whose failure to resolve is a real failure even though the code
    /// null-checks: Construct and FieldRefAccess throw; a FirstMethod/FirstField that lists
    /// several names declares cross-version alternatives, so none matching is an unhandled
    /// rename; ConstructorsWithParameter finding no constructor leaves its patches unbound.
    /// A single-name FirstMethod/FirstField and plain AccessTools lookups return null and are
    /// treated as version-optional.</summary>
    static bool MustResolve(Lookup lk) =>
        lk.Api is "MemberCompat.Construct" or "AccessTools.FieldRefAccess" or "AccessTools.StaticFieldRefAccess" or "MemberCompat.ConstructorsWithParameter"
        || (lk.Api is "MemberCompat.FirstMethod" or "MemberCompat.FirstField" && lk.Names is { Count: >= 2 });

    static string MustResolveReason(Lookup lk) => lk.Api switch
    {
        "MemberCompat.Construct" => "Construct throws MissingMethodException",
        "AccessTools.FieldRefAccess" or "AccessTools.StaticFieldRefAccess" => "FieldRefAccess throws (in a static initialiser: TypeInitializationException)",
        "MemberCompat.ConstructorsWithParameter" => "no constructor matches, so the patch binds nothing",
        _ => "none of the listed alternative names resolves",
    };

    /// <summary>Severity of one Harmony target (see class doc).</summary>
    void Classify(Target t, string tname, bool hasPrepare)
    {
        if (t.Status == "UNDECIDED") { Undecide($"{tname}: {t.Desc}: {string.Join("; ", t.Notes)}"); return; }
        if (!t.Ok)
        {
            var line = $"{tname}: target {t.Status}: {t.Desc}";
            if (!hasPrepare) _failLines.Add(line + "  (no Prepare: Harmony throws during PatchAll)");
            else if (t.Lookup is { } lk)
            {
                if (lk.Collected) _optionalLines.Add(line + "  (one of several collected targets)");
                else if (MustResolve(lk)) _failLines.Add(line + "  (" + MustResolveReason(lk) + "; the class skips itself)");
                else _optionalLines.Add(line + "  (Harmony target of a Prepare-gated class: the class skips itself)");
            }
            else _gatedLines.Add(line + "  (class can skip itself; that feature is off on this version)");
            return;
        }
        TargetsOk++;
        foreach (var (pm, kind) in t.Pms)
        {
            if (kind is "Transpiler" or "ILManipulator") continue;
            foreach (var r in t.Resolved)
                foreach (var p in CheckParams(pm, r))
                {
                    var line = $"{tname}: PARAM-BIND ({kind} {_idx.MethodName(pm)}): {p}";
                    if (hasPrepare) { _gatedLines.Add(line); _o.WriteLine($"        GATED (class has Prepare; expected to skip itself): {p}"); }
                    else { _failLines.Add(line); _o.WriteLine($"        PARAM-BIND FAIL ({kind} {_idx.MethodName(pm)}): {p}"); }
                }
        }
    }

    void AddDump(Target t, string tname, bool hasPrepare)
    {
        var parts = t.Desc.Split("::", 2);
        string type = t.Lookup?.Type ?? parts[0];
        string member = t.DumpMember ?? (t.Lookup?.Names != null ? string.Join("|", t.Lookup.Names) : "-");
        if (t.Lookup is { Api: "AccessTools.PropertyGetter" or "AccessTools.DeclaredPropertyGetter" } && t.Lookup.Names != null) member = "get_" + t.Lookup.Names[0];
        if (t.Lookup is { Api: "AccessTools.PropertySetter" or "AccessTools.DeclaredPropertySetter" } && t.Lookup.Names != null) member = "set_" + t.Lookup.Names[0];
        string pc = t.DumpPc ?? (t.Lookup?.ParamTypes != null ? t.Lookup.ParamTypes.Count.ToString() : "?");
        string ps = t.DumpPs ?? (t.Lookup?.ParamTypes != null ? string.Join(";", t.Lookup.ParamTypes) : "-");
        var bind = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (pm, kind) in t.Pms)
        {
            if (kind is "Transpiler" or "ILManipulator") continue;
            foreach (var ph in MR.GetMethodDefinition(pm).GetParameters())
            {
                var p = MR.GetParameter(ph);
                if (p.SequenceNumber == 0) continue;
                var n = MR.GetString(p.Name);
                if (SpecialParams.Contains(n) || (n.StartsWith("__") && !n.StartsWith("___"))) continue;
                bind.Add(n);
            }
        }
        var declared = t.Lookup == null || t.Lookup.Api.Contains("Declared") ? "declared" : "inherited";
        _dump.Add(string.Join("\t", t.Ok ? "PASS" : (t.Status == "UNDECIDED" ? "UNVERIFIABLE" : "FAIL"),
            "HARMONY", type, member, pc, ps, $"harmony-target {t.Desc} [{declared}]", t.Status,
            tname + (hasPrepare ? " [Prepare]" : ""), bind.Count == 0 ? "-" : string.Join(";", bind)));
    }

    Target TargetFromLookup(Lookup lk, List<(MethodDefinitionHandle mh, string kind)> pms)
    {
        var t = new Target { Lookup = lk, Pms = pms, Desc = lk.Describe() + "  (in " + lk.User + ")" };
        var r = ResolveLookup(lk);
        t.Status = r.Status; t.Ok = r.Ok; t.Resolved = r.Methods; t.Notes.AddRange(r.Notes);
        return t;
    }

    // ------------------------------------------------------------------ lookups

    sealed class LookupRes
    {
        public bool Ok;
        public string Status = "";
        public List<MethodDefinitionHandle> Methods = new();
        public List<string> Notes = new();
        public bool NotGame, Dynamic, Info;
    }

    LookupRes ResolveLookup(Lookup lk)
    {
        var r = new LookupRes();
        if (lk.Api == "MemberCompat.HasParameter") return ResolveHasParameter(lk);
        if (lk.Type == null) { r.Dynamic = true; r.Status = "UNDECIDED"; r.Notes.Add("declaring type is only known at runtime"); return r; }
        if (!G.Types.TryGetValue(lk.Type, out var tdh))
        {
            int lt = lk.Type.IndexOf('<');
            if (lt < 0 || !G.Types.TryGetValue(lk.Type[..lt], out tdh))
            {
                if (!_eval.IsGameType(lk.Type)) { r.NotGame = true; r.Ok = true; r.Status = "NOT A GAME TYPE"; return r; }
                r.Status = "TYPE MISSING"; return r;
            }
        }
        if (lk.Names == null && lk.Api != "MemberCompat.ConstructorsWithParameter")
        { r.Dynamic = true; r.Status = "UNDECIDED"; r.Notes.Add("member name is only known at runtime"); return r; }
        if (lk.ParamsUnknown && lk.Api is "MemberCompat.FirstMethod" or "MemberCompat.Construct")
        { r.Status = "UNDECIDED"; r.Notes.Add("parameter-type array is not a literal new[] { typeof(...) } / Type.EmptyTypes"); return r; }
        var start = new ChainLink(G, tdh, null, 0);
        switch (lk.Api)
        {
            case "MemberCompat.FirstMethod": return FirstMethod(lk, start);
            case "MemberCompat.FirstField": return FirstField(lk, start);
            case "MemberCompat.ConstructorsWithParameter": return CtorsWithParam(lk, start);
            case "MemberCompat.Construct": return Construct(lk, start);
            case "AccessTools.FieldRefAccess": case "AccessTools.StaticFieldRefAccess": return FieldRef(lk, start);
            case "AccessTools.Field": case "AccessTools.DeclaredField": case "Type.GetField":
            {
                var (st, _) = ResolveField(lk.Type, lk.Names![0], lk.Api == "AccessTools.DeclaredField");
                r.Ok = st == "OK"; r.Status = st; return r;
            }
            case "AccessTools.Property": case "AccessTools.DeclaredProperty": case "Type.GetProperty":
            {
                var (st, _) = ResolveProperty(lk.Type, lk.Names![0], lk.Api == "AccessTools.DeclaredProperty");
                r.Ok = st == "OK"; r.Status = st; return r;
            }
            default:
            {
                var api = lk.Api[(lk.Api.IndexOf('.') + 1)..];
                if (lk.Api == "Type.GetMethod") api = "Method";
                if (lk.Api == "Type.GetConstructor") api = "Constructor";
                var (st, res) = ResolveNamed(lk.Type, lk.Names![0], api, lk.ParamTypes?.ToArray(), declaredOnly: api.StartsWith("Declared"), argsUnknown: lk.ParamsUnknown);
                r.Ok = st.StartsWith("OK"); r.Status = st; r.Methods = res; return r;
            }
        }
    }

    List<ChainLink> Chain(ChainLink start) => Resolver.ClassChain(_set, start.Asm, start.H, null, out _);

    string ChainNote(ChainLink start)
    {
        Resolver.ClassChain(_set, start.Asm, start.H, null, out var ext);
        return ext != null && !ext.StartsWith("System.Object [", StringComparison.Ordinal) ? $" (base chain not followed past {ext}; pass --ext)" : "";
    }

    static string Where(ChainLink l, ChainLink start) => l.H == start.H && l.Asm == start.Asm ? "" : " (on base " + l.Asm.TypeName(l.H) + ")";

    LookupRes FirstMethod(Lookup lk, ChainLink start)
    {
        var r = new LookupRes();
        var want = lk.ParamTypes ?? new List<string>();
        var chain = Chain(start);
        int winner = -1;
        for (int i = 0; i < lk.Names!.Count; i++)
        {
            var name = lk.Names[i];
            (ChainLink l, MethodDefinitionHandle h, string display)? exact = null;
            var others = new List<string>();
            foreach (var l in chain)
                foreach (var m in Resolver.Methods(l))
                {
                    if (m.name != name) continue;
                    var sig = l.Asm.R.GetMethodDefinition(m.h).DecodeSignature(SigProvider.Instance, null);
                    if (exact == null && sig.ParameterTypes.SequenceEqual(want)) exact = (l, m.h, m.display + Where(l, start));
                    else others.Add(m.display + Where(l, start));
                }
            string pos = $"[name {i + 1} of {lk.Names.Count}]";
            if (exact != null && winner < 0)
            {
                winner = i;
                if (exact.Value.l.Asm == G) r.Methods.Add(exact.Value.h);
                r.Notes.Add($"resolves to \"{name}\" {pos}: {exact.Value.display}");
            }
            else if (exact != null) r.Notes.Add($"later name \"{name}\" {pos} also exists (not used): {exact.Value.display}");
            else if (others.Count > 0) r.Notes.Add($"name \"{name}\" {pos} exists only with other parameters (skipped): {string.Join(" | ", others)}");
            else r.Notes.Add($"name \"{name}\" {pos} not found{ChainNote(start)}");
        }
        r.Ok = winner >= 0;
        r.Status = r.Ok ? "OK" : "NO NAME RESOLVES";
        return r;
    }

    LookupRes FirstField(Lookup lk, ChainLink start)
    {
        var r = new LookupRes();
        var chain = Chain(start);
        int winner = -1;
        for (int i = 0; i < lk.Names!.Count; i++)
        {
            var name = lk.Names[i];
            string? found = null;
            foreach (var l in chain)
            {
                foreach (var f in Resolver.Fields(l))
                    if (f.name == name) { found = f.display + Where(l, start); break; }
                if (found != null) break;
            }
            string pos = $"[name {i + 1} of {lk.Names.Count}]";
            if (found != null && winner < 0) { winner = i; r.Notes.Add($"resolves to \"{name}\" {pos}: {found}"); }
            else if (found != null) r.Notes.Add($"later name \"{name}\" {pos} also exists (not used): {found}");
            else r.Notes.Add($"name \"{name}\" {pos} not found{ChainNote(start)}");
        }
        r.Ok = winner >= 0;
        r.Status = r.Ok ? "OK" : "NO NAME RESOLVES";
        return r;
    }

    LookupRes CtorsWithParam(Lookup lk, ChainLink start)
    {
        var r = new LookupRes();
        if (lk.ParamName == null || lk.ParamType == null) { r.Status = "UNDECIDED"; r.Notes.Add("parameter name/type not a literal"); return r; }
        foreach (var m in Resolver.Methods(start))
        {
            if (m.name != ".ctor") continue;
            var md = G.R.GetMethodDefinition(m.h);
            if ((md.Attributes & MethodAttributes.Static) != 0) continue;
            var sig = md.DecodeSignature(SigProvider.Instance, null);
            var names = ParamNames(m.h);
            for (int i = 0; i < sig.ParameterTypes.Length; i++)
                if (names[i] == lk.ParamName && sig.ParameterTypes[i] == lk.ParamType) { r.Methods.Add(m.h); break; }
        }
        r.Ok = r.Methods.Count > 0;
        r.Status = r.Ok ? $"OK ({r.Methods.Count} constructor(s))" : "NO CONSTRUCTOR WITH THAT PARAMETER";
        if (!r.Ok)
            foreach (var m in Resolver.Methods(start).Where(m => m.name == ".ctor")) r.Notes.Add("has: " + DescribeGameMethod(m.h));
        return r;
    }

    LookupRes Construct(Lookup lk, ChainLink start)
    {
        var r = new LookupRes();
        var lead = lk.ParamTypes!;
        MethodDefinitionHandle best = default; int bestExtra = int.MaxValue;
        foreach (var m in Resolver.Methods(start))
        {
            if (m.name != ".ctor") continue;
            var md = G.R.GetMethodDefinition(m.h);
            if ((md.Attributes & MethodAttributes.Static) != 0) continue;
            var sig = md.DecodeSignature(SigProvider.Instance, null);
            if (sig.ParameterTypes.Length < lead.Count) continue;
            bool ok = true;
            for (int i = 0; ok && i < lead.Count; i++) ok = sig.ParameterTypes[i] == lead[i];
            var opt = ParamOptional(m.h);
            for (int i = lead.Count; ok && i < sig.ParameterTypes.Length; i++) ok = opt[i];
            if (!ok) continue;
            int extra = sig.ParameterTypes.Length - lead.Count;
            if (extra < bestExtra) { best = m.h; bestExtra = extra; }
        }
        r.Ok = !best.IsNil;
        if (r.Ok) { r.Methods.Add(best); r.Status = "OK"; r.Notes.Add("chosen constructor: " + DescribeGameMethod(best)); }
        else
        {
            r.Status = "NO MATCHING CONSTRUCTOR";
            foreach (var m in Resolver.Methods(start).Where(m => m.name == ".ctor")) r.Notes.Add("has: " + DescribeGameMethod(m.h));
        }
        return r;
    }

    LookupRes FieldRef(Lookup lk, ChainLink start)
    {
        var r = new LookupRes();
        var name = lk.Names![0];
        foreach (var l in Chain(start))
            foreach (var f in Resolver.Fields(l))
            {
                if (f.name != name) continue;
                r.Notes.Add("field: " + f.display + Where(l, start));
                var compat = Assignable(f.type, lk.FieldType ?? "?", out var why);
                if (compat == true) { r.Ok = true; r.Status = "OK"; }
                else if (compat == null) { r.Ok = true; r.Status = "OK-NAME"; r.Notes.Add("type compatibility undecided: " + why); }
                else { r.Status = "FIELD TYPE INCOMPATIBLE"; r.Notes.Add($"field type {f.type} is not assignable to {lk.FieldType}"); }
                return r;
            }
        r.Status = "FIELD MISSING";
        r.Notes.Add($"no field \"{name}\" on {lk.Type} or its bases{ChainNote(start)}");
        return r;
    }

    /// <summary>Is a value of type 'from' assignable to 'to'? null = cannot tell.</summary>
    bool? Assignable(string from, string to, out string why)
    {
        why = "";
        if (from == to || to == "System.Object") return true;
        int lt = from.IndexOf('<');
        var bare = lt >= 0 ? from[..lt] : from;
        var res = _set.Resolve(bare.StartsWith("System.", StringComparison.Ordinal) ? "System.Runtime" : G.Name, bare);
        if (res == null && !G.Types.ContainsKey(bare)) { why = $"{from} is outside the loaded assemblies"; return null; }
        var link = res != null ? new ChainLink(res.Value.asm, res.Value.h, null, 0) : new ChainLink(G, G.Types[bare], null, 0);
        var chain = Resolver.ClassChain(_set, link.Asm, link.H, null, out var ext);
        var unresolved = new List<string>();
        var all = chain.Concat(chain.SelectMany(c => Resolver.InterfaceClosure(_set, c, unresolved))).ToList();
        if (all.Any(c => c.Asm.TypeName(c.H) == to)) return true;
        if (ext != null && !ext.StartsWith("System.Object [", StringComparison.Ordinal)) { why = $"base chain of {from} leaves the loaded assemblies at {ext} (pass --ext)"; return null; }
        return false;
    }

    LookupRes ResolveHasParameter(Lookup lk)
    {
        var r = new LookupRes { Info = true, Ok = true };
        if (lk.Inner == null) { r.Status = "INFO"; r.Notes.Add("method argument is not statically known"); return r; }
        var inner = ResolveLookup(lk.Inner);
        if (!inner.Ok || inner.Methods.Count == 0) { r.Status = "INFO"; r.Notes.Add("method not resolved: " + inner.Status); return r; }
        foreach (var m in inner.Methods)
        {
            var md = G.R.GetMethodDefinition(m);
            var sig = md.DecodeSignature(SigProvider.Instance, null);
            var names = ParamNames(m);
            bool has = Enumerable.Range(0, names.Count).Any(i => names[i] == lk.ParamName && sig.ParameterTypes[i] == lk.ParamType);
            r.Status = "INFO";
            r.Notes.Add($"{(has ? "TRUE" : "FALSE")}: {DescribeGameMethod(m)} {(has ? "has" : "has no")} parameter {lk.ParamType} {lk.ParamName}");
        }
        return r;
    }

    void ReportLookups()
    {
        // MemberCompat's own implementation (reflection over its parameters) is not a lookup site.
        var all = _eval.Lookups.Values.Distinct()
            .Where(l => !l.User.Contains(".MemberCompat::", StringComparison.Ordinal) && !l.User.Contains(".MemberCompat/", StringComparison.Ordinal))
            .OrderBy(l => l.User, StringComparer.Ordinal).ThenBy(l => l.Offset).ToList();
        _o.WriteLine("=== MEMBERCOMPAT / FIELDREFACCESS RESOLUTIONS ===");
        int dynamicCount = 0, notGame = 0, okCount = 0;
        var dynamicLines = new List<string>();
        foreach (var lk in all)
        {
            bool isTarget = _targetLookups.Contains(lk);
            var r = ResolveLookup(lk);
            var tag = isTarget ? $"  [Harmony target of {_targetOwner[lk]}]" : lk.Collected ? "  [collected into a list]" : "";
            bool important = lk.IsMemberCompat || lk.Api.Contains("FieldRefAccess");
            if (r.NotGame) { notGame++; continue; }
            if (r.Dynamic) { dynamicCount++; dynamicLines.Add($"{lk.Describe()} in {lk.User}: {string.Join("; ", r.Notes)}"); continue; }
            if (important)
            {
                var bits = new List<string>(r.Notes);
                if (lk.Api == "MemberCompat.ConstructorsWithParameter") bits.AddRange(r.Methods.Select(DescribeGameMethod));
                var owner = isTarget ? string.Join(", ", _targetOwner[lk].Split(", ").Select(OwnerShort)) : OwnerShort(lk.User);
                var apiShort = lk.Api[(lk.Api.IndexOf('.') + 1)..];
                _resolutionLines.Add(Short($"{owner}: {apiShort}({lk.Type}{(lk.Names != null && lk.Api is not "MemberCompat.Construct" and not "MemberCompat.ConstructorsWithParameter" ? ", " + string.Join("|", lk.Names) : "")}) -> {r.Status}: {string.Join("; ", bits)}"));
                _o.WriteLine($"  {r.Status}: {lk.Describe()}  in {lk.User}{tag}");
                foreach (var n in r.Notes) _o.WriteLine($"      {n}");
                foreach (var m in r.Methods) if (lk.Api != "MemberCompat.FirstMethod" && lk.Api != "MemberCompat.Construct") _o.WriteLine($"      -> {DescribeGameMethod(m)}");
                if (!isTarget && lk.Api != "MemberCompat.HasParameter") AddLookupDump(lk, r);
            }
            if (isTarget || r.Info) continue; // severity of targets is decided in the Harmony section
            if (r.Ok) { okCount++; continue; }
            if (r.Status == "UNDECIDED") { Undecide($"{lk.Describe()} in {lk.User}: {string.Join("; ", r.Notes)}"); continue; }
            var line = $"{r.Status}: {lk.Describe()} in {lk.User}";
            if (MustResolve(lk) && !lk.Collected) _failLines.Add(line + "  (" + MustResolveReason(lk) + ")" + (r.Notes.Count > 0 ? "  [" + string.Join("; ", r.Notes) + "]" : ""));
            else _optionalLines.Add(line);
        }
        _o.WriteLine();
        _o.WriteLine($"=== DYNAMIC LOOKUPS (type or name only known at runtime; not checked) ({dynamicCount}) ===");
        foreach (var l in dynamicLines) _o.WriteLine("  " + l);
        _o.WriteLine($"  ({okCount} non-target lookups resolved; {notGame} lookups on non-game types skipped)");
        _o.WriteLine();
    }

    void AddLookupDump(Lookup lk, LookupRes r)
    {
        string member = lk.Names != null ? string.Join("|", lk.Names) : "-";
        string pc = lk.ParamTypes != null ? lk.ParamTypes.Count.ToString() : "?";
        string ps = lk.ParamTypes != null ? string.Join(";", lk.ParamTypes) : "-";
        string extra = lk.Api switch
        {
            "MemberCompat.ConstructorsWithParameter" => $"param={lk.ParamName}:{lk.ParamType}",
            "AccessTools.FieldRefAccess" => $"fieldType={lk.FieldType}",
            _ => "-",
        };
        _dump.Add(string.Join("\t", r.Ok ? "PASS" : "FAIL", "LOOKUP", lk.Type ?? "-", member, pc, ps,
            $"lookup {lk.Describe()} in {lk.User}", lk.Api, lk.User, extra));
    }

    // ------------------------------------------------------------------ resolution in the game

    (string status, List<MethodDefinitionHandle> res) ResolveNamed(string typeFull, string? name, string api, string[]? argTypes, bool declaredOnly, bool argsUnknown = false)
    {
        var res = new List<MethodDefinitionHandle>();
        if (!G.Types.TryGetValue(typeFull, out var tdh)) return ("TYPE MISSING", res);
        string? mname = api switch
        {
            "DeclaredPropertyGetter" or "PropertyGetter" => "get_" + name,
            "DeclaredPropertySetter" or "PropertySetter" => "set_" + name,
            "DeclaredConstructor" or "Constructor" => ".ctor",
            "StaticConstructor" => ".cctor",
            _ => name,
        };
        if (mname == null) return ("UNDECIDED (no method name)", res);
        var levels = declaredOnly || mname is ".ctor" or ".cctor" ? new List<ChainLink> { new(G, tdh, null, 0) } : Resolver.ClassChain(_set, G, tdh, null, out _);
        foreach (var l in levels)
        {
            if (l.Asm != G) break;
            foreach (var m in Resolver.Methods(l))
            {
                if (m.name != mname) continue;
                if (argTypes != null || api.EndsWith("Constructor"))
                {
                    var sig = l.Asm.R.GetMethodDefinition(m.h).DecodeSignature(SigProvider.Instance, null);
                    if (!argsUnknown && !sig.ParameterTypes.SequenceEqual(argTypes ?? Array.Empty<string>())) continue;
                }
                res.Add(m.h);
            }
            if (res.Count > 0) break; // nearest level wins (FindIncludingBaseTypes semantics)
        }
        if (res.Count == 0)
        {
            var sameName = Resolver.Methods(new ChainLink(G, tdh, null, 0)).Where(m => m.name == mname).Select(m => m.display).ToList();
            return (sameName.Count > 0 ? "ARG-TYPES MISMATCH (has: " + string.Join(" | ", sameName) + ")" : "TARGET MISSING", res);
        }
        if (res.Count > 1 && argsUnknown)
            return ($"OK-NAME ({res.Count} overloads; argument types given but not parsed)", res);
        if (res.Count > 1 && argTypes == null && !api.EndsWith("Constructor") && api != "FirstMethod")
        {
            // AccessTools.Method falls back to the parameterless overload before throwing AmbiguousMatchException
            var noArgs = res.Where(h => G.R.GetMethodDefinition(h).DecodeSignature(SigProvider.Instance, null).ParameterTypes.Length == 0).ToList();
            if (noArgs.Count == 1 && api is "Method" or "DeclaredMethod") return ("OK (parameterless overload of several)", noArgs);
            return ($"AMBIGUOUS ({res.Count} overloads, no argument types given: AmbiguousMatchException)", res);
        }
        return ("OK", res);
    }

    (string, List<MethodDefinitionHandle>) ResolveField(string typeFull, string name, bool declaredOnly)
    {
        if (!G.Types.TryGetValue(typeFull, out var tdh)) return ("TYPE MISSING", new());
        var levels = declaredOnly ? new List<ChainLink> { new(G, tdh, null, 0) } : Resolver.ClassChain(_set, G, tdh, null, out _);
        foreach (var l in levels)
            if (Resolver.Fields(l).Any(f => f.name == name)) return ("OK", new());
        return ("FIELD MISSING", new());
    }

    (string, List<MethodDefinitionHandle>) ResolveProperty(string typeFull, string name, bool declaredOnly)
    {
        if (!G.Types.TryGetValue(typeFull, out var tdh)) return ("TYPE MISSING", new());
        var levels = declaredOnly ? new List<ChainLink> { new(G, tdh, null, 0) } : Resolver.ClassChain(_set, G, tdh, null, out _);
        foreach (var l in levels)
        {
            var r = l.Asm.R;
            foreach (var ph in r.GetTypeDefinition(l.H).GetProperties())
                if (r.GetString(r.GetPropertyDefinition(ph).Name) == name) return ("OK", new());
        }
        return ("PROPERTY MISSING", new());
    }

    string DescribeGameMethod(MethodDefinitionHandle h)
    {
        var r = G.R;
        var md = r.GetMethodDefinition(h);
        var sig = md.DecodeSignature(SigProvider.Instance, null);
        var names = ParamNames(h);
        var opt = ParamOptional(h);
        var ps = sig.ParameterTypes.Select((t, i) => t + " " + (i < names.Count ? names[i] : "?") + (opt[i] ? " = default" : ""));
        return $"{G.TypeName(md.GetDeclaringType())}::{r.GetString(md.Name)}({string.Join(", ", ps)})";
    }

    List<string> ParamNames(MethodDefinitionHandle h)
    {
        var r = G.R;
        var md = r.GetMethodDefinition(h);
        int n = md.DecodeSignature(SigProvider.Instance, null).ParameterTypes.Length;
        var names = Enumerable.Repeat("?", n).ToList();
        foreach (var ph in md.GetParameters())
        {
            var p = r.GetParameter(ph);
            if (p.SequenceNumber > 0 && p.SequenceNumber <= n) names[p.SequenceNumber - 1] = r.GetString(p.Name);
        }
        return names;
    }

    bool[] ParamOptional(MethodDefinitionHandle h)
    {
        var r = G.R;
        var md = r.GetMethodDefinition(h);
        int n = md.DecodeSignature(SigProvider.Instance, null).ParameterTypes.Length;
        var opt = new bool[n];
        foreach (var ph in md.GetParameters())
        {
            var p = r.GetParameter(ph);
            if (p.SequenceNumber > 0 && p.SequenceNumber <= n)
                opt[p.SequenceNumber - 1] = (p.Attributes & (ParameterAttributes.Optional | ParameterAttributes.HasDefault)) != 0;
        }
        return opt;
    }

    List<string> CheckParams(MethodDefinitionHandle patch, MethodDefinitionHandle target)
    {
        var problems = new List<string>();
        var pmd = MR.GetMethodDefinition(patch);
        var targetNames = ParamNames(target);
        var tmd = G.R.GetMethodDefinition(target);
        var declType = tmd.GetDeclaringType();
        foreach (var ph in pmd.GetParameters())
        {
            var p = MR.GetParameter(ph);
            if (p.SequenceNumber == 0) continue;
            var name = MR.GetString(p.Name);
            foreach (var a in _idx.Attributes.Where(a => a.OwnerHandle == (EntityHandle)ph && a.AttrType == "HarmonyLib.HarmonyArgument"))
                if (a.Value?.FixedArguments.FirstOrDefault().Value is string orig) name = orig;
            if (SpecialParams.Contains(name)) continue;
            if (name.StartsWith("__") && name.Length > 2 && name.Skip(2).All(char.IsDigit))
            {
                int idx = int.Parse(name[2..]);
                if (idx >= targetNames.Count) problems.Add($"index parameter {name} out of range ({targetNames.Count} params)");
                continue;
            }
            if (name.StartsWith("___"))
            {
                var field = name[3..];
                var chain = Resolver.ClassChain(_set, G, declType, null, out var ext);
                bool found = chain.Any(l => Resolver.Fields(l).Any(f => f.name == field));
                if (!found) problems.Add(ext != null && !_set.HasExt
                    ? $"field injection {name}: field '{field}' not found in sts2 part of chain (chain continues into {ext}; UNDECIDED)"
                    : $"field injection {name}: field '{field}' not found on {G.TypeName(declType)} or its bases");
                continue;
            }
            if (!targetNames.Contains(name))
                problems.Add($"parameter '{name}' not found on target {DescribeGameMethod(target)}");
        }
        return problems;
    }
}

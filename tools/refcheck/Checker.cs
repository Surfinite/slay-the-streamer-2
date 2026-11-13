using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;

namespace RefCheck;

public sealed class RefRow
{
    public string Kind = "";          // TYPE | METHOD | FIELD
    public string TypeFull = "";      // canonical (generic definition) name of the parent type
    public string ParentDisplay = ""; // parent as referenced (may be an instantiation)
    public string Member = "";
    public string Key = "";           // method key or field type
    public string Display = "";
    public ImmutableArray<string> Params = ImmutableArray<string>.Empty;
    public string Ret = "";
    public bool IsInstance;
    public int GenericArity;
    public LookupResult Result = new();
    public EntityHandle Handle;

    public string Tag => Result.Status switch
    {
        St.Pass or St.PassViaBase or St.PassExternal => "PASS",
        St.UnverifiableExternalBase => "UNVERIFIABLE",
        St.Skipped => "SKIP",
        _ => "FAIL",
    };
}

public sealed class Checker
{
    readonly Asm _mod;
    readonly AsmSet _set;
    readonly ModIndex _idx;
    readonly TextWriter _o;
    public readonly List<RefRow> Rows = new();
    public readonly List<string> DumpExtra = new();
    int _loadBreaking;
    int _orphans;
    bool _ignoresAccess;

    public Checker(Asm mod, AsmSet set, TextWriter o)
    {
        _mod = mod; _set = set; _o = o;
        _idx = new ModIndex(mod);
    }

    MetadataReader MR => _mod.R;

    bool IsGameScoped(TypeReferenceHandle h) => string.Equals(Names.TypeRefScopeAssembly(MR, h), _set.Game.Name, StringComparison.OrdinalIgnoreCase);

    public void Run()
    {
        var gameAsmRef = MR.AssemblyReferences.Select(h => MR.GetAssemblyReference(h)).FirstOrDefault(a => MR.GetString(a.Name) == _set.Game.Name);
        _o.WriteLine($"mod:  {_mod.Path}  (assembly {_mod.Name}, mvid {MR.GetGuid(MR.GetModuleDefinition().Mvid)})");
        _o.WriteLine($"game: {_set.Game.Path}  (assembly {_set.Game.Name} {_set.Game.R.GetAssemblyDefinition().Version}, mvid {_set.Game.R.GetGuid(_set.Game.R.GetModuleDefinition().Mvid)})");
        _o.WriteLine($"mod's AssemblyRef to {_set.Game.Name}: " + (gameAsmRef.Name.IsNil ? "NONE" : gameAsmRef.Version.ToString()));
        _o.WriteLine($"external base resolution (--ext): {(_set.HasExt ? "ON" : "OFF")}");
        _o.WriteLine();

        // [assembly: IgnoresAccessChecksTo("sts2")] makes CoreCLR skip visibility checks (publicized builds)
        _ignoresAccess = _idx.Attributes.Any(a => a.Owner == "assembly" && a.AttrType.EndsWith("IgnoresAccessChecksToAttribute", StringComparison.Ordinal)
            && a.Value?.FixedArguments.FirstOrDefault().Value is string s && string.Equals(s, _set.Game.Name, StringComparison.OrdinalIgnoreCase));
        _o.WriteLine($"[assembly: IgnoresAccessChecksTo(\"{_set.Game.Name}\")]: {(_ignoresAccess ? "PRESENT (non-public access is reported as PASS)" : "absent (non-public game members fail with MethodAccessException/FieldAccessException/TypeAccessException)")}");
        _o.WriteLine();

        CheckTypeRefs();
        CheckMemberRefs();
        if (_ignoresAccess)
            foreach (var r in Rows.Where(r => r.Result.Status == St.AccessFail))
            {
                r.Result.Status = St.Pass;
                r.Result.Note = "non-public (" + r.Result.Access + ") but IgnoresAccessChecksTo is present";
            }
        PrintFailures();
        CheckModTypes();
        var h = new HarmonyCheck(_mod, _set, _idx, _o, DumpExtra);
        h.Run();
        PrintSummary(h);
    }

    void CheckTypeRefs()
    {
        foreach (var trh in MR.TypeReferences)
        {
            if (!IsGameScoped(trh)) continue;
            var full = Names.TypeRefFullName(MR, trh);
            var row = new RefRow { Kind = "TYPE", TypeFull = full, ParentDisplay = full, Display = "type " + full, Handle = trh };
            if (_set.Game.Types.TryGetValue(full, out var tdh))
            {
                var acc = Resolver.TypeAccess(_set.Game, tdh);
                row.Result.Access = acc;
                row.Result.Status = Resolver.AccessOk(acc) ? St.Pass : St.AccessFail;
                row.Result.FoundOn = full;
            }
            else if (_set.Game.Forwarders.TryGetValue(full, out var fwd))
            {
                row.Result.Status = St.UnverifiableExternalBase;
                row.Result.Note = "type forwarded to " + fwd;
            }
            else
            {
                row.Result.Status = St.TypeMissing;
                // same simple name elsewhere? (moved namespace / arity change)
                var simple = full.Split('/', '.').Last();
                var bare = simple.Split('`')[0];
                foreach (var k in _set.Game.Types.Keys)
                {
                    var ks = k.Split('/', '.').Last();
                    if (ks == simple || ks.Split('`')[0] == bare) row.Result.Candidates.Add("type " + k);
                }
            }
            Rows.Add(row);
        }
    }

    void CheckMemberRefs()
    {
        foreach (var mrh in MR.MemberReferences)
        {
            var mr = MR.GetMemberReference(mrh);
            var parent = mr.Parent;
            EntityHandle underlying;
            string parentDisplay;
            switch (parent.Kind)
            {
                case HandleKind.TypeReference:
                    underlying = parent;
                    parentDisplay = Names.TypeRefFullName(MR, (TypeReferenceHandle)parent);
                    break;
                case HandleKind.TypeSpecification:
                {
                    var tsh = (TypeSpecificationHandle)parent;
                    if (Resolver.TryGenericInst(MR, tsh, null, out var und, out _))
                    {
                        underlying = und;
                        parentDisplay = MR.GetTypeSpecification(tsh).DecodeSignature(SigProvider.Instance, null);
                    }
                    else
                    {
                        // array / pointer / generic-parameter typespec: if it is an array of an sts2
                        // type, its methods (Get/Set/Address/.ctor) are runtime-provided.
                        var col = new TypeRefCollector();
                        try { MR.GetTypeSpecification(tsh).DecodeSignature(col, null); } catch { }
                        if (col.Found.Any(IsGameScoped))
                        {
                            var disp = MR.GetTypeSpecification(tsh).DecodeSignature(SigProvider.Instance, null);
                            var r = new RefRow { Kind = mr.GetKind() == MemberReferenceKind.Method ? "METHOD" : "FIELD", TypeFull = disp, ParentDisplay = disp, Member = MR.GetString(mr.Name), Handle = mrh };
                            r.Display = r.Kind.ToLowerInvariant() + " " + disp + "::" + r.Member + " (array/typespec member)";
                            r.Result.Status = St.Skipped;
                            r.Result.Note = "runtime-provided array method; element type checked as a TYPE ref";
                            Rows.Add(r);
                        }
                        continue;
                    }
                    break;
                }
                default:
                    continue; // MethodDef (vararg), ModuleRef, TypeDef (mod's own)
            }
            if (underlying.Kind != HandleKind.TypeReference || !IsGameScoped((TypeReferenceHandle)underlying)) continue;

            var typeFull = Names.TypeRefFullName(MR, (TypeReferenceHandle)underlying);
            var name = MR.GetString(mr.Name);
            var row = new RefRow { TypeFull = typeFull, ParentDisplay = parentDisplay, Member = name, Handle = mrh };
            if (mr.GetKind() == MemberReferenceKind.Method)
            {
                var sig = mr.DecodeMethodSignature(SigProvider.Instance, null);
                row.Kind = "METHOD";
                row.Key = Resolver.MethodKey(sig);
                row.Display = Resolver.MethodDisplay(parentDisplay, name, sig);
                row.Params = sig.ParameterTypes;
                row.Ret = sig.ReturnType;
                row.IsInstance = sig.Header.IsInstance;
                row.GenericArity = sig.GenericParameterCount;
            }
            else
            {
                var ft = mr.DecodeFieldSignature(SigProvider.Instance, null);
                row.Kind = "FIELD";
                row.Key = ft;
                row.Ret = ft;
                row.Display = Resolver.FieldDisplay(parentDisplay, name, ft);
            }

            if (!_set.Game.Types.TryGetValue(typeFull, out var tdh))
            {
                row.Result.Status = St.TypeMissing;
                row.Result.Note = "declaring type " + typeFull + " missing in game";
            }
            else
            {
                row.Result = Resolver.FindMember(_set, new ChainLink(_set.Game, tdh, null, 0), name, row.Kind == "METHOD", row.Key);
            }
            Rows.Add(row);
        }
    }

    void PrintFailures()
    {
        var fails = Rows.Where(r => r.Tag == "FAIL").OrderBy(r => r.Kind == "TYPE" ? 0 : 1).ThenBy(r => r.Display, StringComparer.Ordinal).ToList();
        _o.WriteLine($"=== FAILURES ({fails.Count}) ===");
        foreach (var r in fails)
        {
            _o.WriteLine();
            _o.WriteLine($"[{StatusLabel(r.Result.Status)}] {r.Display}");
            if (r.Result.FoundOn != null) _o.WriteLine($"    resolved on: {r.Result.FoundOn} (access: {r.Result.Access})");
            if (r.Result.Note != null) _o.WriteLine($"    note: {r.Result.Note}");
            foreach (var c in r.Result.Candidates) _o.WriteLine($"    game has: {c}");
            PrintUsers(r);
        }
        _o.WriteLine();

        var unv = Rows.Where(r => r.Tag == "UNVERIFIABLE").OrderBy(r => r.Display, StringComparer.Ordinal).ToList();
        _o.WriteLine($"=== UNVERIFIABLE-EXTERNAL-BASE ({unv.Count}) ===");
        foreach (var r in unv)
        {
            _o.WriteLine($"  {r.Display}");
            if (r.Result.Note != null) _o.WriteLine($"      {r.Result.Note}");
            foreach (var c in r.Result.Candidates) _o.WriteLine($"      sts2 same-name: {c}");
        }
        _o.WriteLine();
    }

    void PrintUsers(RefRow r)
    {
        SortedSet<string> il, st;
        if (r.Handle.Kind == HandleKind.TypeReference) (il, st) = _idx.UsersOfType((TypeReferenceHandle)r.Handle);
        else (il, st) = _idx.UsersOfMember((MemberReferenceHandle)r.Handle);
        if (il.Count == 0 && st.Count == 0) _o.WriteLine("    used by: no IL user found (likely attribute or signature use)");
        foreach (var u in il) _o.WriteLine($"    used by IL: {u}");
        foreach (var u in st) _o.WriteLine($"    structural: {u}");
    }

    static string StatusLabel(St s) => s switch
    {
        St.TypeMissing => "TYPE MISSING",
        St.MemberMissing => "MEMBER MISSING",
        St.SignatureMismatch => "SIGNATURE MISMATCH",
        St.AccessFail => "ACCESS (not public/protected)",
        _ => s.ToString(),
    };

    // ---------------------------------------------------------------- mod types vs sts2 bases

    void CheckModTypes()
    {
        _o.WriteLine("=== MOD TYPES DERIVING FROM / IMPLEMENTING sts2 TYPES ===");
        foreach (var th in MR.TypeDefinitions)
        {
            var td = MR.GetTypeDefinition(th);
            var tname = _mod.TypeName(th);
            bool isAbstract = (td.Attributes & TypeAttributes.Abstract) != 0;
            bool isInterface = (td.Attributes & TypeAttributes.Interface) != 0;

            // Base chain: does it reach sts2?
            var chain = Resolver.ClassChain(_set, _mod, th, null, out var extStop);
            // System.Object has no abstract members and its virtuals (ToString/Equals/GetHashCode/Finalize)
            // are handled below; stopping there loses nothing.
            if (extStop != null && extStop.StartsWith("System.Object [", StringComparison.Ordinal)) extStop = null;
            var objectVirtuals = new HashSet<string> { "ToString", "Equals", "GetHashCode", "Finalize" };
            var sts2Bases = chain.Where(l => l.Asm == _set.Game).ToList();
            var sts2Ifaces = new List<(ChainLink link, string disp)>();
            var missingIfaces = new List<string>();
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var ifh = MR.GetInterfaceImplementation(ih).Interface;
                if (!_idx.Mentions(ifh).Any(IsGameScoped)) continue;
                var disp = DisplayHandle(ifh);
                var link = Resolver.ResolveHandle(_set, _mod, ifh, null, 1, out var ext);
                if (link == null)
                {
                    // An external (e.g. BCL) generic interface over sts2 type args is fine as long as
                    // those args resolve (they are TYPE rows); only an unresolvable sts2 interface breaks.
                    if (ext != null && ext.Contains("MISSING in game")) missingIfaces.Add(disp + " -> " + ext);
                    continue;
                }
                if (link.Value.Asm == _set.Game) sts2Ifaces.Add((link.Value, disp));
            }
            bool baseMentionsGame = !td.BaseType.IsNil && _idx.Mentions(td.BaseType).Any(IsGameScoped);
            if (sts2Bases.Count == 0 && sts2Ifaces.Count == 0 && missingIfaces.Count == 0 && !baseMentionsGame) continue;

            _o.WriteLine();
            var baseDisp = td.BaseType.IsNil ? "(none)" : DisplayHandle(td.BaseType);
            _o.WriteLine($"{tname}{(isAbstract ? " [abstract]" : "")}{(isInterface ? " [interface]" : "")} : base {baseDisp}"
                + (sts2Ifaces.Count + missingIfaces.Count > 0 ? "; implements " + string.Join(", ", sts2Ifaces.Select(i => i.disp).Concat(missingIfaces)) : ""));
            if (extStop != null && extStop.Contains("MISSING in game"))
            {
                _o.WriteLine($"    LOAD-BREAKING: base chain does not resolve in game ({extStop})");
                _loadBreaking++;
            }
            foreach (var m in missingIfaces) { _o.WriteLine($"    LOAD-BREAKING: interface does not resolve: {m}"); _loadBreaking++; }

            // Gather the implementing side: virtual/instance methods on the mod type and every class in its chain.
            // For interface checks we need: methods in the class chain (any asm) + MethodImpl rows of the mod chain.
            var explicitImpls = new HashSet<string>(StringComparer.Ordinal); // "ifaceType::name|key"
            foreach (var l in chain)
            {
                foreach (var mih in l.Asm.R.GetTypeDefinition(l.H).GetMethodImplementations())
                {
                    var mi = l.Asm.R.GetMethodImplementation(mih);
                    var decl = DescribeMethodHandle(l.Asm, mi.MethodDeclaration, l.Ctx);
                    if (decl != null) explicitImpls.Add(decl.Value.owner + "::" + decl.Value.name + "|" + decl.Value.key);
                }
            }

            if (!isInterface)
            {
                // Interfaces: every abstract interface method (incl. inherited interfaces) needs an implementation.
                var allIfaces = new List<ChainLink>();
                var unresolved = new List<string>();
                foreach (var (link, _) in sts2Ifaces)
                {
                    allIfaces.Add(link);
                    allIfaces.AddRange(Resolver.InterfaceClosure(_set, link, unresolved).Where(x => x.Asm == _set.Game));
                }
                foreach (var il in allIfaces)
                {
                    var iname = il.Asm.TypeName(il.H);
                    foreach (var im in Resolver.Methods(il))
                    {
                        var imd = il.Asm.R.GetMethodDefinition(im.h);
                        bool isStatic = (imd.Attributes & MethodAttributes.Static) != 0;
                        if (!im.isAbstract) continue; // default interface method / static non-abstract
                        // implicit impl: public virtual instance method with same name+key in the class chain
                        bool ok = false;
                        foreach (var l in chain)
                        {
                            foreach (var cm in Resolver.Methods(l))
                                if (cm.name == im.name && cm.key == im.key && (cm.isVirtual || isStatic)) { ok = true; break; }
                            if (ok) break;
                        }
                        if (!ok && explicitImpls.Contains(iname + "::" + im.name + "|" + im.key)) ok = true;
                        if (!ok)
                        {
                            // generic interfaces: a MethodImpl MemberRef signature uses !0 placeholders,
                            // so compare against the open (unsubstituted) interface signature too
                            var openKey = Resolver.MethodKey(imd.DecodeSignature(SigProvider.Instance, null));
                            ok = explicitImpls.Contains(iname + "::" + im.name + "|" + openKey);
                        }
                        if (ok) continue;
                        var nameMatches = new List<string>();
                        foreach (var l in chain)
                            foreach (var cm in Resolver.Methods(l))
                                if (cm.name == im.name || cm.name.EndsWith("." + im.name, StringComparison.Ordinal)) nameMatches.Add(cm.display);
                        _o.WriteLine($"    LOAD-BREAKING: interface method not implemented: {im.display}");
                        foreach (var n in nameMatches) _o.WriteLine($"        mod/chain has: {n}");
                        _loadBreaking++;
                    }
                }

                // Abstract base members: if the type is concrete, every abstract method in the chain must be overridden.
                if (!isAbstract)
                {
                    for (int i = 0; i < chain.Count; i++)
                    {
                        var l = chain[i];
                        foreach (var am in Resolver.Methods(l))
                        {
                            if (!am.isAbstract) continue;
                            bool ok = false;
                            for (int j = 0; j < i && !ok; j++)
                                foreach (var dm in Resolver.Methods(chain[j]))
                                    if (dm.name == am.name && dm.key == am.key && dm.isVirtual && !dm.isAbstract) { ok = true; break; }
                            if (!ok && explicitImpls.Contains(l.Asm.TypeName(l.H) + "::" + am.name + "|" + am.key)) ok = true;
                            if (ok) continue;
                            _o.WriteLine($"    LOAD-BREAKING: abstract member not overridden: {am.display}");
                            for (int j = 0; j < i; j++)
                                foreach (var dm in Resolver.Methods(chain[j]))
                                    if (dm.name == am.name) _o.WriteLine($"        chain has: {dm.display}");
                            _loadBreaking++;
                        }
                    }
                }
                if (extStop != null) _o.WriteLine($"    note: base chain leaves the checked set at {extStop}; abstract members beyond it not checked");
            }

            // Orphaned overrides: mod virtual methods without NewSlot (C# 'override') whose name+sig match
            // no virtual in the base chain. Runtime does not throw; the override silently becomes a new slot.
            var self = chain[0];
            foreach (var mm in Resolver.Methods(self))
            {
                if (!mm.isVirtual || mm.newSlot) continue;
                bool matched = false;
                for (int j = 1; j < chain.Count && !matched; j++)
                    foreach (var bm in Resolver.Methods(chain[j]))
                        if (bm.name == mm.name && bm.key == mm.key && bm.isVirtual) { matched = true; break; }
                if (matched) continue;
                if (objectVirtuals.Contains(mm.name) && !chain.Any(l => l.Asm.TypeName(l.H) == "System.Object")) continue; // System.Object not loaded
                if (extStop != null)
                {
                    _o.WriteLine($"    override not found in checked chain (chain leaves set at {extStop}): {mm.display}");
                    continue;
                }
                _o.WriteLine($"    ORPHANED OVERRIDE (silently stops overriding; no exception): {mm.display}");
                for (int j = 1; j < chain.Count; j++)
                    foreach (var bm in Resolver.Methods(chain[j]))
                        if (bm.name == mm.name) _o.WriteLine($"        base has: {bm.display}");
                _orphans++;
            }
            var ovCount = Resolver.Methods(self).Count(m => m.isVirtual && !m.newSlot);
            _o.WriteLine($"    checked: {sts2Bases.Count} sts2 base level(s), {sts2Ifaces.Count} sts2 interface(s), {ovCount} override(s)");
        }
        _o.WriteLine();
    }

    string DisplayHandle(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeReference => Names.TypeRefFullName(MR, (TypeReferenceHandle)h),
        HandleKind.TypeDefinition => _mod.TypeName((TypeDefinitionHandle)h),
        HandleKind.TypeSpecification => MR.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(SigProvider.Instance, null),
        _ => h.Kind.ToString(),
    };

    /// <summary>Owner/name/key of a MethodImpl declaration (MethodDef or MemberRef).</summary>
    (string owner, string name, string key)? DescribeMethodHandle(Asm a, EntityHandle h, string[]? ctx)
    {
        var r = a.R;
        if (h.Kind == HandleKind.MethodDefinition)
        {
            var md = r.GetMethodDefinition((MethodDefinitionHandle)h);
            return (a.TypeName(md.GetDeclaringType()), r.GetString(md.Name), Resolver.MethodKey(md.DecodeSignature(SigProvider.Instance, null)));
        }
        if (h.Kind == HandleKind.MemberReference)
        {
            var mr = r.GetMemberReference((MemberReferenceHandle)h);
            string owner = mr.Parent.Kind switch
            {
                HandleKind.TypeReference => Names.TypeRefFullName(r, (TypeReferenceHandle)mr.Parent),
                HandleKind.TypeDefinition => a.TypeName((TypeDefinitionHandle)mr.Parent),
                HandleKind.TypeSpecification => Resolver.TryGenericInst(r, (TypeSpecificationHandle)mr.Parent, null, out var und, out _)
                    ? (und.Kind == HandleKind.TypeReference ? Names.TypeRefFullName(r, (TypeReferenceHandle)und) : a.TypeName((TypeDefinitionHandle)und))
                    : "?",
                _ => "?",
            };
            if (mr.GetKind() != MemberReferenceKind.Method) return null;
            return (owner, r.GetString(mr.Name), Resolver.MethodKey(mr.DecodeMethodSignature(SigProvider.Instance, null)));
        }
        return null;
    }

    // ---------------------------------------------------------------- summary / dump

    void PrintSummary(HarmonyCheck h)
    {
        _o.WriteLine("=== SUMMARY ===");
        foreach (var kind in new[] { "TYPE", "METHOD", "FIELD" })
        {
            var rs = Rows.Where(r => r.Kind == kind).ToList();
            var byStatus = rs.GroupBy(r => r.Result.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
            _o.WriteLine($"  {kind,-6} refs checked: {rs.Count,4}   {string.Join("  ", byStatus)}");
        }
        var all = Rows;
        _o.WriteLine($"  TOTAL  refs: {all.Count}  PASS={all.Count(r => r.Tag == "PASS")}  FAIL={all.Count(r => r.Tag == "FAIL")}  UNVERIFIABLE={all.Count(r => r.Tag == "UNVERIFIABLE")}  SKIP={all.Count(r => r.Tag == "SKIP")}");
        _o.WriteLine($"  mod-type LOAD-BREAKING gaps: {_loadBreaking}   ORPHANED overrides: {_orphans}");
        _o.WriteLine($"  Harmony + lookups: {h.Summary}");
        int compiledFail = all.Count(r => r.Tag == "FAIL");
        AnyFail = compiledFail > 0 || _loadBreaking > 0 || h.Failures > 0;
        foreach (var line in h.ResolutionLines) _o.WriteLine("REFCHECK-RESOLUTION " + line);
        // one machine-readable line (run.ps1 parses it)
        _o.WriteLine($"REFCHECK-SUMMARY compiledRefs={all.Count} compiledFail={compiledFail} unverifiable={all.Count(r => r.Tag == "UNVERIFIABLE")} loadBreaking={_loadBreaking} orphanedOverrides={_orphans} harmonyTargetsOk={h.TargetsOk} fail={h.Failures} gated={h.Gated} undecided={h.Undecided} optionalNotFound={h.Optional} result={(AnyFail ? "FAIL" : "PASS")}");
    }

    /// <summary>True when any real failure was found (compiled ref, load-breaking gap, Harmony/lookup FAIL).</summary>
    public bool AnyFail { get; private set; }

    public void WriteDump(string path)
    {
        var lines = new List<string>();
        foreach (var r in Rows)
        {
            // TSV: tag, kind, type, member, paramCount, params(;), canonical display, status
            var pc = r.Kind == "METHOD" ? r.Params.Length.ToString() : "-";
            var ps = r.Kind == "METHOD" ? string.Join(";", r.Params) : (r.Kind == "FIELD" ? r.Key : "-");
            SortedSet<string> il, st;
            if (r.Handle.Kind == HandleKind.TypeReference) (il, st) = _idx.UsersOfType((TypeReferenceHandle)r.Handle);
            else (il, st) = _idx.UsersOfMember((MemberReferenceHandle)r.Handle);
            var users = string.Join(" | ", il.Concat(st));
            lines.Add(string.Join("\t", r.Tag, r.Kind, r.TypeFull, r.Kind == "TYPE" ? "-" : r.Member, pc, ps, r.Display, r.Result.Status.ToString(), users.Length == 0 ? "-" : users, "-"));
        }
        lines.AddRange(DumpExtra);
        lines.Sort((a, b) =>
        {
            var aa = a.Split('\t'); var bb = b.Split('\t');
            int c = string.CompareOrdinal(aa[6], bb[6]);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });
        File.WriteAllLines(path, new[] { "#tag\tkind\ttype\tmember\tparamCount\tparams_or_fieldType\tcanonical\tstatus\tusers\tharmony_bind_names" }.Concat(lines));
    }
}

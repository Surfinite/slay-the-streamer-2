using System.Text;
using System.Text.RegularExpressions;

namespace RefCheck;

/// <summary>
/// TEXTUAL (regex-level) check of a refcheck dump against a flat ILSpy-decompiled source tree
/// (one directory per namespace, one .cs per top-level type). Used for game versions where no
/// DLL is on disk. Precision is limited: see the report header written by Run().
/// </summary>
public static class Textual
{
    sealed class TypeDecl
    {
        public string File = "";
        public string Text = "";       // cleaned file text (comments/strings blanked)
        public string Ns = "";
        public string Name = "";
        public int Arity;
        public string Kind = "";
        public TypeDecl? Parent;
        public int HeaderStart, BodyStart = -1, BodyEnd = -1;
        public string Header = "";     // text between the name and '{'
        public string MemberText = ""; // body with nested blocks blanked IN PLACE (same indices as Text, offset BodyStart+1)
        public List<string> Usings = new();
        public string FullName => (Parent != null ? Parent.FullName + "/" : (Ns.Length > 0 ? Ns + "." : "")) + Name + (Arity > 0 ? "`" + Arity : "");
        public string SimpleName => Name;
    }

    static readonly Dictionary<(string, int), List<TypeDecl>> BySimple = new();
    static readonly Dictionary<string, TypeDecl> ByFull = new(StringComparer.Ordinal);

    static readonly Regex TypeDeclRx = new(@"\b(class|struct|interface|enum|record)\s+([A-Za-z_]\w*)\s*(<[^{;()]*?>)?", RegexOptions.Compiled);
    static readonly Regex DelegateRx = new(@"\bdelegate\s+[\w\.<>\[\],\s\?\(\)]+?\s+([A-Za-z_]\w*)\s*(<[^;(]*?>)?\s*\(", RegexOptions.Compiled);
    static readonly Regex NsRx = new(@"^\s*namespace\s+([\w\.]+)\s*[;{]", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex UsingRx = new(@"^\s*using\s+([\w\.]+)\s*;", RegexOptions.Compiled | RegexOptions.Multiline);

    static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "virtual", "override", "abstract", "sealed", "async", "extern",
        "unsafe", "new", "readonly", "partial", "required", "const", "volatile", "fixed", "implicit", "explicit",
    };

    static readonly Dictionary<string, string> Keyword = new(StringComparer.Ordinal)
    {
        ["Boolean"] = "bool", ["Byte"] = "byte", ["SByte"] = "sbyte", ["Char"] = "char", ["Int16"] = "short", ["UInt16"] = "ushort",
        ["Int32"] = "int", ["UInt32"] = "uint", ["Int64"] = "long", ["UInt64"] = "ulong", ["Single"] = "float", ["Double"] = "double",
        ["Decimal"] = "decimal", ["String"] = "string", ["Object"] = "object", ["Void"] = "void", ["IntPtr"] = "nint", ["UIntPtr"] = "nuint",
    };

    // ------------------------------------------------------------------ source cleaning / indexing

    static string Clean(string s)
    {
        var o = s.ToCharArray();
        int i = 0, n = s.Length;
        void Blank(int a, int b) { for (int k = a; k < b && k < n; k++) if (o[k] != '\n') o[k] = ' '; }
        while (i < n)
        {
            char c = s[i];
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { int e = s.IndexOf('\n', i); if (e < 0) e = n; Blank(i, e); i = e; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*') { int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 ? n : e + 2; Blank(i, e); i = e; continue; }
            if (c == '\'')
            {
                int e = i + 1;
                while (e < n && s[e] != '\'') { if (s[e] == '\\') e++; e++; if (e - i > 12) break; }
                if (e < n && s[e] == '\'') { Blank(i + 1, e); i = e + 1; continue; }
                i++; continue;
            }
            if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (s[i + 1] == '"' || ((s[i + 1] == '@' || s[i + 1] == '$') && i + 2 < n && s[i + 2] == '"'))))
            {
                int q = i; bool verbatim = false;
                while (s[q] != '"') { if (s[q] == '@') verbatim = true; q++; }
                if (q + 2 < n && s[q + 1] == '"' && s[q + 2] == '"')
                {
                    int e = s.IndexOf("\"\"\"", q + 3, StringComparison.Ordinal); e = e < 0 ? n : e + 3;
                    Blank(i, e); i = e; continue;
                }
                int k = q + 1;
                while (k < n)
                {
                    if (verbatim) { if (s[k] == '"') { if (k + 1 < n && s[k + 1] == '"') { k += 2; continue; } break; } }
                    else { if (s[k] == '\\') { k += 2; continue; } if (s[k] == '"' || s[k] == '\n') break; }
                    k++;
                }
                Blank(q + 1, k);
                for (int z = i; z < q; z++) o[z] = ' ';
                i = k + 1; continue;
            }
            i++;
        }
        return new string(o);
    }

    static int MatchBrace(string t, int open, char a = '{', char b = '}')
    {
        int d = 0;
        for (int i = open; i < t.Length; i++)
        {
            if (t[i] == a) d++;
            else if (t[i] == b) { d--; if (d == 0) return i; }
        }
        return -1;
    }

    static int Arity(string? gen) => string.IsNullOrEmpty(gen) ? 0 : HarmonyCheck.SplitTopLevel(gen.Trim().TrimStart('<').TrimEnd('>')).Count;

    static void IndexTree(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel.StartsWith("obj" + Path.DirectorySeparatorChar) || rel.StartsWith("bin" + Path.DirectorySeparatorChar)) continue;
            var raw = File.ReadAllText(path);
            var text = Clean(raw);
            var nsm = NsRx.Match(text);
            var dirNs = Path.GetDirectoryName(rel) ?? "";
            var ns = nsm.Success ? nsm.Groups[1].Value : dirNs;
            var usings = UsingRx.Matches(text).Select(m => m.Groups[1].Value).ToList();
            var decls = new List<TypeDecl>();
            foreach (Match m in TypeDeclRx.Matches(text))
            {
                var name = m.Groups[2].Value;
                if (name is "struct" or "class") continue; // "record struct X" handled by the struct match
                // reject "where T : class" etc.: the keyword must be preceded by a modifier, '}' ';' '{' ']' or start
                int p = m.Index - 1;
                while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
                if (p >= 0 && !(char.IsLetter(text[p]) || text[p] is '}' or ';' or '{' or ']')) continue;
                if (p >= 0 && char.IsLetter(text[p]))
                {
                    int ws = p; while (ws >= 0 && char.IsLetter(text[ws])) ws--;
                    var word = text.Substring(ws + 1, p - ws);
                    if (!Modifiers.Contains(word) && word != "record" && word != "ref") continue;
                }
                var d = new TypeDecl { File = path, Text = text, Ns = ns, Name = name, Arity = Arity(m.Groups[3].Value), Kind = m.Groups[1].Value, HeaderStart = m.Index, Usings = usings };
                int brace = -1, semi = -1;
                for (int k = m.Index + m.Length; k < text.Length; k++)
                {
                    if (text[k] == '{') { brace = k; break; }
                    if (text[k] == ';') { semi = k; break; }
                    if (text[k] == '(') { int c = MatchBrace(text, k, '(', ')'); if (c > 0) k = c; }
                }
                if (brace >= 0)
                {
                    d.BodyStart = brace; d.BodyEnd = MatchBrace(text, brace);
                    d.Header = text.Substring(m.Index + m.Length, brace - (m.Index + m.Length));
                }
                else d.Header = semi > 0 ? text.Substring(m.Index + m.Length, semi - (m.Index + m.Length)) : "";
                decls.Add(d);
            }
            foreach (Match m in DelegateRx.Matches(text))
                decls.Add(new TypeDecl { File = path, Text = text, Ns = ns, Name = m.Groups[1].Value, Arity = Arity(m.Groups[2].Value), Kind = "delegate", HeaderStart = m.Index, Usings = usings });
            // nesting: innermost enclosing body
            foreach (var d in decls)
            {
                TypeDecl? best = null;
                foreach (var o in decls)
                    if (o != d && o.BodyStart >= 0 && o.BodyStart < d.HeaderStart && d.HeaderStart < o.BodyEnd && (best == null || o.BodyStart > best.BodyStart)) best = o;
                d.Parent = best;
            }
            foreach (var d in decls)
            {
                if (d.BodyStart >= 0 && d.BodyEnd > d.BodyStart)
                {
                    var body = text.ToCharArray(d.BodyStart + 1, d.BodyEnd - d.BodyStart - 1);
                    int depth = 0;
                    for (int k = 0; k < body.Length; k++)
                    {
                        char c = body[k];
                        if (c == '{') { if (depth > 0) body[k] = ' '; depth++; }
                        else if (c == '}') { depth--; if (depth > 0) body[k] = ' '; }
                        else if (depth > 0 && c != '\n') body[k] = ' ';
                    }
                    d.MemberText = new string(body);
                }
                var key = (d.Name, d.Arity);
                if (!BySimple.TryGetValue(key, out var l)) BySimple[key] = l = new();
                l.Add(d);
                ByFull.TryAdd(d.FullName, d);
            }
        }
    }

    // ------------------------------------------------------------------ type-name normalisation

    static string StripMods(string c)
    {
        while (true)
        {
            int i = c.IndexOf(" modreq(", StringComparison.Ordinal);
            if (i < 0) i = c.IndexOf(" modopt(", StringComparison.Ordinal);
            if (i < 0) break;
            int open = c.IndexOf('(', i);
            int close = MatchBrace(c, open, '(', ')');
            c = c.Remove(i, close - i + 1);
        }
        return c.Replace(" pinned", "");
    }

    /// <summary>Canonical (metadata) type string -> simple C#-like form for textual comparison.</summary>
    static string NormCanon(string c)
    {
        c = StripMods(c).Replace("&", "");
        c = Regex.Replace(c, @"[!\w\.\/`]+", m =>
        {
            var tok = m.Value;
            if (tok.StartsWith("!")) return "*";
            if (tok.StartsWith("System.") && !tok.Contains('/') && Keyword.TryGetValue(tok[7..], out var kw) && tok.Count(ch => ch == '.') == 1) return kw;
            var last = tok.Split('/', '.').Last();
            int bt = last.IndexOf('`');
            return bt >= 0 ? last[..bt] : last;
        });
        c = UnwrapGeneric(c, "Nullable", inner => inner);
        c = UnwrapGeneric(c, "ValueTuple", inner => "(" + inner + ")");
        return Regex.Replace(c, @"[\s\?]", "");
    }

    static string UnwrapGeneric(string c, string name, Func<string, string> f)
    {
        while (true)
        {
            var m = Regex.Match(c, @"(?<![\w])" + name + "<");
            if (!m.Success) return c;
            int open = m.Index + name.Length;
            int close = MatchBrace(c, open, '<', '>');
            if (close < 0) return c;
            var inner = c.Substring(open + 1, close - open - 1);
            c = c[..m.Index] + f(inner) + c[(close + 1)..];
        }
    }

    /// <summary>Source type text -> simple form.</summary>
    static string NormSrc(string t)
    {
        t = t.Replace("global::", "");
        t = NormTuples(t);
        t = Regex.Replace(t, @"\s+", "");
        t = t.Replace("?", "");
        t = Regex.Replace(t, @"(?:[A-Za-z_]\w*\.)+([A-Za-z_]\w*)", "$1");
        t = Regex.Replace(t, @"[A-Za-z_]\w*", m => Keyword.TryGetValue(m.Value, out var kw) ? kw : m.Value);
        return t;
    }

    static string NormTuples(string t)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '(')
            {
                int close = MatchBrace(t, i, '(', ')');
                if (close < 0) { sb.Append(t[i]); continue; }
                var parts = HarmonyCheck.SplitTopLevel(t.Substring(i + 1, close - i - 1));
                sb.Append('(').Append(string.Join(",", parts.Select(p => DropName(NormTuples(p.Trim()))))).Append(')');
                i = close;
            }
            else sb.Append(t[i]);
        }
        return sb.ToString();
    }

    /// <summary>"Type name" -> "Type" (top-level last whitespace split).</summary>
    static string DropName(string p)
    {
        int depth = 0, lastSpace = -1;
        for (int i = 0; i < p.Length; i++)
        {
            if (p[i] is '<' or '(' or '[') depth++;
            else if (p[i] is '>' or ')' or ']') depth--;
            else if (char.IsWhiteSpace(p[i]) && depth == 0) lastSpace = i;
        }
        return lastSpace > 0 ? p[..lastSpace].Trim() : p;
    }

    static bool TypeEq(string canonSimple, string srcSimple)
    {
        if (canonSimple == srcSimple) return true;
        if (!canonSimple.Contains('*')) return false;
        var rx = "^" + Regex.Escape(canonSimple).Replace(@"\*", ".+?") + "$";
        return Regex.IsMatch(srcSimple, rx);
    }

    // ------------------------------------------------------------------ member parsing

    sealed record SrcParam(string Type, string Name, bool HasDefault = false);
    sealed record SrcMember(string Kind, string Name, string Ret, List<SrcParam> Params, int GenArity, int Index, TypeDecl Owner);

    static List<SrcParam> ParseParams(string inner)
    {
        var list = new List<SrcParam>();
        if (inner.Trim().Length == 0) return list;
        foreach (var raw in HarmonyCheck.SplitTopLevel(inner))
        {
            var p = raw.Trim();
            p = Regex.Replace(p, @"^(\[[^\]]*\]\s*)+", "");
            // default value
            int depth = 0, eq = -1;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] is '<' or '(' or '[') depth++;
                else if (p[i] is '>' or ')' or ']') depth--;
                else if (p[i] == '=' && depth == 0) { eq = i; break; }
            }
            if (eq > 0) p = p[..eq].Trim();
            while (true)
            {
                var m = Regex.Match(p, @"^(this|ref|out|in|params|scoped|readonly)\s+");
                if (!m.Success) break;
                p = p[m.Length..];
            }
            var type = DropName(p);
            var name = p.Length > type.Length ? p[type.Length..].Trim() : "";
            list.Add(new SrcParam(type, name, eq > 0));
        }
        return list;
    }

    /// <summary>Text between the previous member boundary and position idx, minus modifiers/attributes.</summary>
    static string PrecedingType(string mt, int idx) => PrecedingType(mt, idx, out _);

    /// <summary>boundary = the char that ended the backwards walk ('\0' = start of text). A member
    /// declaration starts after ';', '{', '}' or the start; '(' '<' '[' mean we are inside an
    /// argument/parameter list, i.e. not a declaration.</summary>
    static string PrecedingType(string mt, int idx, out char boundary)
    {
        int k = idx - 1, depth = 0;
        boundary = '\0';
        while (k >= 0)
        {
            char c = mt[k];
            if (c is '>' or ')' or ']') depth++;
            else if (c is '<' or '(' or '[') { if (depth == 0) { boundary = c; break; } depth--; }
            else if (depth == 0 && c is ';' or '{' or '}') { boundary = c; break; }
            k--;
        }
        var s = mt.Substring(k + 1, idx - k - 1);
        s = Regex.Replace(s, @"\[[^\[\]]*\]", " ");
        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        // drop modifiers from the front
        while (words.Count > 0 && Modifiers.Contains(words[0])) words.RemoveAt(0);
        return string.Join(" ", words);
    }

    static IEnumerable<SrcMember> FindMembers(TypeDecl t, string name)
    {
        var mt = t.MemberText;
        if (mt.Length == 0) yield break;
        int baseIdx = t.BodyStart + 1;
        if (name == ".ctor" || name == ".cctor")
        {
            // primary constructor: "class Name(params) : Base"
            var hdr = t.Header.TrimStart();
            if (name == ".ctor" && hdr.StartsWith("("))
            {
                int close = MatchBrace(hdr, 0, '(', ')');
                if (close > 0) yield return new SrcMember("ctor", name, "void", ParseParams(hdr.Substring(1, close - 1)), 0, t.HeaderStart, t);
            }
            foreach (Match m in Regex.Matches(mt, @"\b" + Regex.Escape(t.Name) + @"\s*\("))
            {
                var pre = PrecedingType(mt, m.Index);
                bool isStatic = Regex.IsMatch(mt.Substring(Math.Max(0, m.Index - 40), Math.Min(40, m.Index)), @"\bstatic\s+$");
                if (pre.Length != 0) continue; // a return type precedes => not a constructor
                if ((name == ".cctor") != isStatic) continue;
                int open = m.Index + m.Length - 1, close = MatchBrace(mt, open, '(', ')');
                if (close < 0) continue;
                yield return new SrcMember("ctor", name, "void", ParseParams(mt.Substring(open + 1, close - open - 1)), 0, baseIdx + m.Index, t);
            }
            yield break;
        }
        if (name.StartsWith("get_") || name.StartsWith("set_"))
        {
            var prop = name[4..];
            if (prop == "Item")
            {
                foreach (Match m in Regex.Matches(mt, @"\bthis\s*\["))
                {
                    int open = m.Index + m.Length - 1, close = MatchBrace(mt, open, '[', ']');
                    if (close < 0) continue;
                    yield return new SrcMember("indexer", name, PrecedingType(mt, m.Index), ParseParams(mt.Substring(open + 1, close - open - 1)), 0, baseIdx + m.Index, t);
                }
            }
            foreach (Match m in Regex.Matches(mt, @"(?<![\w\.])" + Regex.Escape(prop) + @"\s*(\{|=>)"))
            {
                var ret = PrecedingType(mt, m.Index, out var boundary);
                if (boundary is '(' or '<' or '[') continue;
                if (ret.Length == 0 || ret.Contains('=') || ret.StartsWith("return") || Regex.IsMatch(ret, @"^(class|struct|interface|enum|record)\b")) continue;
                yield return new SrcMember("property", name, ret, new(), 0, baseIdx + m.Index, t);
            }
            // literal accessor method (ILSpy emits these for odd cases)
        }
        if (name.StartsWith("add_") || name.StartsWith("remove_"))
        {
            var ev = name[(name.IndexOf('_') + 1)..];
            foreach (Match m in Regex.Matches(mt, @"\bevent\s+[^;{]*?\b" + Regex.Escape(ev) + @"\s*[;{]"))
                yield return new SrcMember("event", name, "", new(), 0, baseIdx + m.Index, t);
        }
        if (name.StartsWith("op_"))
        {
            var sym = name switch
            {
                "op_Implicit" => "implicit operator", "op_Explicit" => "explicit operator",
                "op_Equality" => "operator ==", "op_Inequality" => "operator !=", "op_Addition" => "operator +",
                "op_Subtraction" => "operator -", "op_Multiply" => "operator *", "op_Division" => "operator /",
                "op_LessThan" => "operator <", "op_GreaterThan" => "operator >", "op_LessThanOrEqual" => "operator <=",
                "op_GreaterThanOrEqual" => "operator >=", "op_UnaryNegation" => "operator -", "op_LogicalNot" => "operator !",
                _ => null,
            };
            if (sym != null)
                foreach (Match m in Regex.Matches(mt, Regex.Escape(sym).Replace(@"\ ", @"\s+") + @"\s*\("))
                {
                    int open = m.Index + m.Length - 1, close = MatchBrace(mt, open, '(', ')');
                    if (close < 0) continue;
                    yield return new SrcMember("operator", name, "", ParseParams(mt.Substring(open + 1, close - open - 1)), 0, baseIdx + m.Index, t);
                }
        }
        // ordinary method
        foreach (Match m in Regex.Matches(mt, @"(?<![\w\.])" + Regex.Escape(name) + @"\s*(<(?:[^<>]|<[^<>]*>)*>)?\s*\("))
        {
            var ret = PrecedingType(mt, m.Index, out var boundary);
            if (boundary is '(' or '<' or '[') continue;
            if (ret.Length == 0 || ret.Contains("=>") || ret.Contains('=') || Regex.IsMatch(ret, @"^(return|await|new|throw|case|is|as)\b")) continue;
            int open = m.Index + m.Length - 1, close = MatchBrace(mt, open, '(', ')');
            if (close < 0) continue;
            yield return new SrcMember("method", name, ret, ParseParams(mt.Substring(open + 1, close - open - 1)), Arity(m.Groups[1].Value), baseIdx + m.Index, t);
        }
    }

    static IEnumerable<(string type, string name)> FindFields(TypeDecl t, string name)
    {
        var mt = t.MemberText;
        foreach (Match m in Regex.Matches(mt, @"(?<![\w\.])" + Regex.Escape(name) + @"\s*(=(?![=>])|;|,)"))
        {
            var pre = PrecedingType(mt, m.Index, out var boundary);
            if (pre.Length == 0 || boundary is '(' or '<' or '[') continue;
            if (pre.Contains('=') || pre.Contains('(') && !pre.TrimStart().StartsWith("(")) continue; // expression / parameter context
            // "int a, b;" style: the type is before the first declarator; accept the name anyway
            yield return (pre.TrimEnd(','), name);
        }
        if (t.Kind == "enum")
            foreach (Match m in Regex.Matches(mt, @"(?<![\w\.])" + Regex.Escape(name) + @"\s*(=|,|$)", RegexOptions.Multiline))
                yield return ("<enum member>", name);
    }

    // ------------------------------------------------------------------ type lookup / base chain

    static TypeDecl? Locate(string canonical)
    {
        if (ByFull.TryGetValue(canonical, out var d)) return d;
        return null;
    }

    static List<TypeDecl> Bases(TypeDecl t, List<string> external)
    {
        var res = new List<TypeDecl>();
        var h = t.Header;
        int colon = -1, depth = 0;
        for (int i = 0; i < h.Length; i++)
        {
            if (h[i] is '<' or '(') depth++;
            else if (h[i] is '>' or ')') depth--;
            else if (h[i] == ':' && depth == 0) { colon = i; break; }
        }
        if (colon < 0) return res;
        var list = h[(colon + 1)..];
        var w = Regex.Match(list, @"\bwhere\b");
        if (w.Success) list = list[..w.Index];
        foreach (var raw in HarmonyCheck.SplitTopLevel(list))
        {
            var b = raw.Trim().Replace("global::", "");
            if (b.Length == 0) continue;
            int lt = b.IndexOf('<');
            var nameq = lt >= 0 ? b[..lt] : b;
            int ar = lt >= 0 ? Arity(b[lt..]) : 0;
            var simple = nameq.Split('.').Last().Trim();
            var qual = nameq.Contains('.') ? nameq[..nameq.LastIndexOf('.')] : null;
            if (!BySimple.TryGetValue((simple, ar), out var cands)) { external.Add(b); continue; }
            // a nested candidate only counts when it is nested in this type or one of its enclosing types
            bool InScope(TypeDecl c)
            {
                if (c.Parent == null) return c.Ns == t.Ns || t.Usings.Contains(c.Ns);
                for (var e = t; e != null; e = e.Parent) if (c.Parent == e) return true;
                return false;
            }
            var pick = cands.Where(c => qual != null ? (c.Parent == null && c.Ns.EndsWith(qual, StringComparison.Ordinal)) || (c.Parent?.Name == qual.Split('.').Last())
                                                     : InScope(c)).ToList();
            if (pick.Count == 0) { external.Add(b); continue; }
            res.AddRange(pick);
        }
        return res;
    }

    /// <summary>Type itself, then (unless declaredOnly) its textual base chain, breadth-first.</summary>
    static List<TypeDecl> Chain(TypeDecl t, bool declaredOnly, List<string> external)
    {
        var res = new List<TypeDecl> { t };
        if (declaredOnly) return res;
        var q = new Queue<TypeDecl>(); q.Enqueue(t);
        var seen = new HashSet<TypeDecl> { t };
        while (q.Count > 0 && res.Count < 40)
        {
            foreach (var b in Bases(q.Dequeue(), external))
                if (seen.Add(b)) { res.Add(b); q.Enqueue(b); }
        }
        return res;
    }

    // ------------------------------------------------------------------ main

    sealed record Row(string Tag, string Kind, string Type, string Member, string ParamCount, string Params, string Canonical, string Status, string Users, string Bind);

    public static int Run(string dumpFile, string dir, string? outFile)
    {
        IndexTree(dir);
        var rows = File.ReadAllLines(dumpFile).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l =>
        {
            var c = l.Split('\t');
            string g(int i) => i < c.Length ? c[i] : "-";
            return new Row(g(0), g(1), g(2), g(3), g(4), g(5), g(6), g(7), g(8), g(9));
        }).ToList();

        var results = new List<(Row row, string verdict, string detail)>();
        foreach (var r in rows)
        {
            try { results.Add((r, Classify(r, out var detail), detail)); }
            catch (Exception e) { results.Add((r, "NEEDS-MANUAL-CHECK", "textual checker error: " + e.Message)); }
        }

        var o = new StringWriter();
        o.WriteLine("TEXTUAL CHECK (regex-level) of " + Path.GetFullPath(dumpFile));
        o.WriteLine("  against decompiled tree " + Path.GetFullPath(dir) + $"  ({ByFull.Count} type declarations indexed)");
        o.WriteLine("  CONFIRMED          = type found / member declaration with same name, same parameter count, and every parameter simple type matching");
        o.WriteLine("                       (return type also compared for methods/fields/properties when it could be extracted)");
        o.WriteLine("  CONFIRMED-NAME     = declaration with that name found; types not comparable textually (events, enum members, unparsed)");
        o.WriteLine("  ABSENT             = no declaration of that name in the type nor in its textual base chain (chain fully inside the tree)");
        o.WriteLine("  NEEDS-MANUAL-CHECK = anything else (overload/parameter-type/return-type differences, external base, parse trouble)");
        o.WriteLine("  Simple names only: namespaces are dropped, nullable '?' is ignored, ref/out/in are ignored, modreq/modopt ignored.");
        o.WriteLine();
        foreach (var kind in new[] { "TYPE", "METHOD", "FIELD", "HARMONY", "LOOKUP" })
        {
            var ks = results.Where(x => x.row.Kind == kind).ToList();
            o.WriteLine($"{kind,-8} {ks.Count,4}: " + string.Join("  ", ks.GroupBy(x => x.verdict).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        }
        o.WriteLine();
        foreach (var verdict in new[] { "ABSENT", "NEEDS-MANUAL-CHECK", "CONFIRMED-NAME" })
        {
            var vs = results.Where(x => x.verdict == verdict).OrderBy(x => x.row.Kind).ThenBy(x => x.row.Canonical, StringComparer.Ordinal).ToList();
            o.WriteLine($"=== {verdict} ({vs.Count}) ===");
            foreach (var (row, _, detail) in vs)
            {
                o.WriteLine($"  [{row.Kind}] {row.Canonical}");
                if (detail.Length > 0) foreach (var dl in detail.Split('\n')) o.WriteLine("      " + dl);
                if (row.Users != "-") o.WriteLine("      users: " + row.Users);
            }
            o.WriteLine();
        }
        var text = o.ToString();
        Console.Write(text.Length > 0 ? string.Join("\n", text.Split('\n').Take(12)) + "\n" : "");
        if (outFile != null) { File.WriteAllText(outFile, text); Console.WriteLine("textual report written: " + Path.GetFullPath(outFile)); }
        else Console.Write(text);
        return 0;
    }

    static string Classify(Row r, out string detail)
    {
        detail = "";
        var t = Locate(r.Type);
        if (t == null)
        {
            var simple = r.Type.Split('/', '.').Last();
            int bt = simple.IndexOf('`');
            var bare = bt >= 0 ? simple[..bt] : simple;
            var hints = BySimple.Where(kv => kv.Key.Item1 == bare).SelectMany(kv => kv.Value).Select(d => d.FullName).Take(5).ToList();
            detail = "type " + r.Type + " not found in tree" + (hints.Count > 0 ? "; same simple name exists as: " + string.Join(", ", hints) : "");
            return hints.Count > 0 ? "NEEDS-MANUAL-CHECK" : "ABSENT";
        }
        if (r.Kind == "TYPE") { detail = ""; return "CONFIRMED"; }
        if (r.Kind == "HARMONY" && (r.Member is "-" or "" || r.Status.StartsWith("UNDECIDED")))
        {
            detail = "Harmony target not statically known (" + r.Status + ")";
            return "NEEDS-MANUAL-CHECK";
        }

        if (r.Kind == "LOOKUP") return ClassifyLookup(r, t, out detail);
        if (r.Kind == "HARMONY" && r.Member.Contains('|'))
        {
            // MemberCompat.FirstMethod target: the first name (in order) that matches wins.
            var names = r.Member.Split('|');
            var notes = new List<string>();
            string worst = "ABSENT";
            for (int i = 0; i < names.Length; i++)
            {
                var v = Classify(r with { Member = names[i] }, out var d);
                if (v == "CONFIRMED")
                {
                    detail = string.Join("\n", notes.Append($"resolves to \"{names[i]}\" [name {i + 1} of {names.Length}]" + (d.Length > 0 ? ": " + d : "")));
                    return v;
                }
                notes.Add($"name \"{names[i]}\" [name {i + 1} of {names.Length}]: {v}" + (d.Length > 0 ? " (" + d.Replace("\n", " ") + ")" : ""));
                if (v == "NEEDS-MANUAL-CHECK") worst = v;
            }
            detail = string.Join("\n", notes);
            return worst;
        }

        var external = new List<string>();
        bool isHarmony = r.Kind == "HARMONY";
        bool declaredOnly = r.Member is ".ctor" or ".cctor"
            || (isHarmony && (r.Canonical.EndsWith("[declared]", StringComparison.Ordinal)
                || (!r.Canonical.EndsWith("[inherited]", StringComparison.Ordinal) && !r.Canonical.Contains("(via AccessTools.Method ") && !r.Canonical.Contains("(via AccessTools.PropertyGetter "))));
        var chain = Chain(t, declaredOnly, external);
        external.RemoveAll(e => e is "object" or "Object" or "System.Object");

        if (r.Kind == "FIELD")
        {
            var want = NormCanon(r.Params);
            var found = chain.SelectMany(c => FindFields(c, r.Member).Select(f => (c, f))).ToList();
            if (found.Count == 0) return Missing(r, chain, external, out detail);
            foreach (var (c, f) in found)
            {
                if (f.type == "<enum member>") { detail = "enum member"; return "CONFIRMED-NAME"; }
                if (TypeEq(want, NormSrc(f.type))) { detail = c == t ? "" : "via base " + c.FullName; return "CONFIRMED"; }
            }
            detail = "field found but type differs: want " + want + "; source has " + string.Join(" | ", found.Select(x => NormSrc(x.f.type) + " in " + x.c.FullName));
            return "NEEDS-MANUAL-CHECK";
        }

        // METHOD / HARMONY
        var wantParams = r.ParamCount is "?" or "-" ? null : (r.Params == "-" || r.Params.Length == 0 ? new List<string>() : r.Params.Split(';').ToList());
        string? wantRet = null;
        int wantGen = 0;
        if (!isHarmony)
        {
            // canonical: "[instance|static] Ret Type::Name<`n>(params)"
            var canon = r.Canonical;
            var head = canon[..canon.IndexOf("::", StringComparison.Ordinal)];
            head = Regex.Replace(head, @"^(instance|static)\s+", "");
            int sp = LastTopLevelSpace(head);
            wantRet = sp > 0 ? head[..sp] : null;
            var gm = Regex.Match(canon, @"::[^(]*<`(\d+)>\(");
            if (gm.Success) wantGen = int.Parse(gm.Groups[1].Value);
            if (wantParams != null && r.ParamCount != "-" && int.TryParse(r.ParamCount, out var pcount) && pcount != wantParams.Count) wantParams = HarmonyCheck.SplitTopLevel(string.Join(",", wantParams)); // defensive
        }

        var members = chain.SelectMany(c => FindMembers(c, r.Member)).ToList();
        if (members.Count == 0)
        {
            // compiler-synthesised members that ILSpy does not print
            if (r.Member == ".ctor" && (wantParams?.Count ?? 0) == 0 && t.Kind is "class" or "struct" or "record"
                && !FindMembers(t, ".ctor").Any() && !t.Header.TrimStart().StartsWith("("))
            {
                detail = "implicit default constructor (no constructor declared in source)";
                return "CONFIRMED-NAME";
            }
            if (t.Kind == "record" && r.Member is "op_Equality" or "op_Inequality" or "Equals" or "GetHashCode" or "ToString"
                or "get_EqualityContract" or "PrintMembers" or "<Clone>$" or "Deconstruct")
            {
                detail = "record-synthesised member (record " + t.FullName + ")";
                return "CONFIRMED-NAME";
            }
            if (t.Kind == "struct" && r.Member == ".ctor" && (wantParams?.Count ?? 0) == 0)
            {
                detail = "struct default constructor";
                return "CONFIRMED-NAME";
            }
            return Missing(r, chain, external, out detail);
        }

        var lines = new List<string>();
        SrcMember? exact = null;
        foreach (var m in members)
        {
            string why = "";
            bool ok = true;
            if (m.Kind is "event") { detail = "event declaration found"; return "CONFIRMED-NAME"; }
            if (m.Kind == "property")
            {
                // get_X: ret = property type; set_X: single param of the property type
                var propType = NormSrc(m.Ret);
                string? want = r.Member.StartsWith("get_") ? (wantRet != null ? NormCanon(wantRet) : null)
                    : wantParams is { Count: 1 } ? NormCanon(wantParams[0]) : null;
                if (want == null) { exact = m; break; }
                if (TypeEq(want, propType))
                {
                    if (r.Member.StartsWith("set_") && !HasSetter(m)) { ok = false; why = "property has no set/init accessor in source"; }
                    else { exact = m; break; }
                }
                else { ok = false; why = "property type " + propType + " vs wanted " + want; }
                lines.Add($"{m.Owner.FullName}::{m.Name} [property] {(ok ? "" : "MISMATCH: " + why)}");
                continue;
            }
            var srcPs = m.Params.Select(p => NormSrc(p.Type)).ToList();
            if (wantParams != null)
            {
                if (srcPs.Count != wantParams.Count) { ok = false; why = $"param count {srcPs.Count} vs {wantParams.Count}"; }
                else
                    for (int i = 0; i < srcPs.Count; i++)
                        if (!TypeEq(NormCanon(wantParams[i]), srcPs[i])) { ok = false; why = $"param {i}: {srcPs[i]} vs {NormCanon(wantParams[i])}"; break; }
            }
            if (ok && wantGen != m.GenArity && m.Kind == "method") { ok = false; why = $"generic arity {m.GenArity} vs {wantGen}"; }
            if (ok && wantRet != null && m.Kind == "method" && !TypeEq(NormCanon(wantRet), NormSrc(m.Ret))) { ok = false; why = $"return type {NormSrc(m.Ret)} vs {NormCanon(wantRet)}"; }
            lines.Add($"{m.Owner.FullName}::{m.Name}({string.Join(", ", m.Params.Select(p => p.Type + " " + p.Name))}) -> {m.Ret} {(ok ? "" : "MISMATCH: " + why)}");
            if (ok && exact == null) exact = m;
        }

        if (exact == null)
        {
            detail = "name present but no matching declaration:\n" + string.Join("\n", lines.Distinct().Take(8).Select(l => "  source: " + l));
            return "NEEDS-MANUAL-CHECK";
        }

        if (isHarmony)
        {
            var problems = new List<string>();
            if (wantParams == null)
            {
                var sameName = members.Where(m => m.Owner == exact.Owner && m.Kind == exact.Kind).ToList();
                if (sameName.Count > 1 && exact.Kind == "method") problems.Add($"{sameName.Count} overloads named {r.Member} on {exact.Owner.FullName} and no argument types given (Harmony would be ambiguous)");
            }
            if (r.Bind != "-")
            {
                foreach (var b in r.Bind.Split(';'))
                {
                    if (b.StartsWith("___"))
                    {
                        var f = b[3..];
                        var fext = new List<string>();
                        if (!Chain(exact.Owner, false, fext).Any(c => FindFields(c, f).Any()))
                            problems.Add($"injected field {b}: no field '{f}' found textually" + (fext.Count > 0 ? " (base chain leaves tree: " + string.Join(", ", fext) + ")" : ""));
                    }
                    else if (!exact.Params.Any(p => p.Name == b))
                        problems.Add($"patch parameter '{b}' not among target parameters ({string.Join(", ", exact.Params.Select(p => p.Name))})");
                }
            }
            if (problems.Count > 0) { detail = string.Join("\n", problems) + "\nresolved: " + lines.FirstOrDefault(); return "NEEDS-MANUAL-CHECK"; }
            detail = "";
            return "CONFIRMED";
        }
        detail = exact.Owner == t ? "" : "via base " + exact.Owner.FullName;
        return "CONFIRMED";
    }

    /// <summary>LOOKUP rows: MemberCompat / FieldRefAccess lookups that are not Harmony targets.</summary>
    static string ClassifyLookup(Row r, TypeDecl t, out string detail)
    {
        var api = r.Status; // the dump stores the API in the status column for LOOKUP rows
        var external = new List<string>();
        var chain = Chain(t, false, external);
        external.RemoveAll(e => e is "object" or "Object" or "System.Object");
        var names = r.Member == "-" ? Array.Empty<string>() : r.Member.Split('|');
        var notes = new List<string>();
        switch (api)
        {
            case "MemberCompat.FirstField":
            case "AccessTools.FieldRefAccess":
            case "AccessTools.StaticFieldRefAccess":
            {
                string? wantType = r.Bind.StartsWith("fieldType=") ? NormCanon(r.Bind["fieldType=".Length..]) : null;
                for (int i = 0; i < names.Length; i++)
                {
                    var hit = chain.SelectMany(c => FindFields(c, names[i]).Select(f => (c, f))).FirstOrDefault();
                    if (hit.c == null) { notes.Add($"name \"{names[i]}\" [{i + 1} of {names.Length}] not declared"); continue; }
                    var srcType = NormSrc(hit.f.type);
                    notes.Add($"resolves to \"{names[i]}\" [{i + 1} of {names.Length}]: {hit.f.type} in {hit.c.FullName}");
                    detail = string.Join("\n", notes);
                    if (wantType != null && !TypeEq(wantType, srcType))
                    {
                        detail += $"\nfield type {srcType} vs FieldRefAccess type {wantType}: assignability not checked textually";
                        return "NEEDS-MANUAL-CHECK";
                    }
                    return "CONFIRMED";
                }
                break;
            }
            case "MemberCompat.FirstMethod":
            {
                var want = r.Params == "-" || r.Params.Length == 0 ? new List<string>() : r.Params.Split(';').ToList();
                for (int i = 0; i < names.Length; i++)
                {
                    var ms = chain.SelectMany(c => FindMembers(c, names[i])).Where(m => m.Kind == "method").ToList();
                    var match = ms.FirstOrDefault(m => m.Params.Count == want.Count
                        && m.Params.Select((p, k) => TypeEq(NormCanon(want[k]), NormSrc(p.Type))).All(b => b));
                    if (match != null)
                    {
                        notes.Add($"resolves to \"{names[i]}\" [{i + 1} of {names.Length}]: {match.Owner.FullName}::{match.Name}({string.Join(", ", match.Params.Select(p => p.Type))})");
                        detail = string.Join("\n", notes);
                        return "CONFIRMED";
                    }
                    notes.Add(ms.Count > 0 ? $"name \"{names[i]}\" [{i + 1} of {names.Length}] exists only with other parameters" : $"name \"{names[i]}\" [{i + 1} of {names.Length}] not declared");
                }
                break;
            }
            case "MemberCompat.ConstructorsWithParameter":
            {
                var spec = r.Bind.StartsWith("param=") ? r.Bind["param=".Length..] : "";
                int colon = spec.IndexOf(':');
                var pname = colon > 0 ? spec[..colon] : spec;
                var ptype = colon > 0 ? NormCanon(spec[(colon + 1)..]) : "";
                var ctors = FindMembers(t, ".ctor").Where(c => c.Params.Any(p => p.Name == pname && TypeEq(ptype, NormSrc(p.Type)))).ToList();
                detail = $"{ctors.Count} constructor(s) with parameter {ptype} {pname}";
                if (ctors.Count > 0) return "CONFIRMED";
                notes.Add(detail);
                break;
            }
            case "MemberCompat.Construct":
            {
                var lead = r.Params == "-" || r.Params.Length == 0 ? new List<string>() : r.Params.Split(';').ToList();
                foreach (var c in FindMembers(t, ".ctor"))
                {
                    if (c.Params.Count < lead.Count) continue;
                    bool ok = true;
                    for (int i = 0; ok && i < lead.Count; i++) ok = TypeEq(NormCanon(lead[i]), NormSrc(c.Params[i].Type));
                    for (int i = lead.Count; ok && i < c.Params.Count; i++) ok = c.Params[i].HasDefault;
                    if (!ok) continue;
                    detail = $"chosen constructor: {t.FullName}({string.Join(", ", c.Params.Select(p => p.Type + " " + p.Name + (p.HasDefault ? " = ..." : "")))})";
                    return "CONFIRMED";
                }
                notes.Add("no constructor with those leading parameter types whose remaining parameters all have defaults");
                break;
            }
            default:
                detail = "lookup API " + api + " not checked textually";
                return "NEEDS-MANUAL-CHECK";
        }
        detail = string.Join("\n", notes);
        if (external.Count > 0) { detail += "\nbase chain leaves the tree at " + string.Join(", ", external.Distinct()); return "NEEDS-MANUAL-CHECK"; }
        return "ABSENT";
    }

    static bool HasSetter(SrcMember m)
    {
        var text = m.Owner.Text;
        int i = text.IndexOf('{', m.Index);
        int arrow = text.IndexOf("=>", m.Index, StringComparison.Ordinal);
        if (i < 0 || (arrow >= 0 && arrow < i)) return false;
        int close = MatchBrace(text, i);
        if (close < 0) return true;
        var block = text.Substring(i, close - i);
        return Regex.IsMatch(block, @"\b(set|init)\b");
    }

    static int LastTopLevelSpace(string s)
    {
        int depth = 0, last = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '<' or '(') depth++;
            else if (s[i] is '>' or ')') depth--;
            else if (s[i] == ' ' && depth == 0) last = i;
        }
        return last;
    }

    static string Missing(Row r, List<TypeDecl> chain, List<string> external, out string detail)
    {
        if (external.Count > 0)
        {
            detail = "not declared in " + string.Join(" -> ", chain.Select(c => c.FullName)) + "; base chain leaves the tree at " + string.Join(", ", external.Distinct());
            return "NEEDS-MANUAL-CHECK";
        }
        detail = "no declaration named " + r.Member + " in " + string.Join(" -> ", chain.Select(c => c.FullName));
        return "ABSENT";
    }
}

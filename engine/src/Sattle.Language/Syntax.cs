using System.Globalization;
using System.Text;
using Sattle.Core;

namespace Sattle.Language;

public readonly record struct Pos(int Line, int Col)
{
    public override string ToString() => $"{Line}:{Col}";
}

public enum T { Ident, Int, Dec, Str, Instant, Duration, Version, Digest, Sym, Doc, Eof }

public sealed record Token(T Kind, string Text, Pos Pos);

/// <summary>Typed parse error with a position (the "check" command reports these).</summary>
public sealed class SyntaxError(string message, Pos pos) : Exception(message) { public Pos Pos { get; } = pos; }

public static class Lexer
{
    static readonly string[] Syms = ["<->", "]->", "|>", "->", "-[", "<-[", "]-", "==", "!=", "<=", ">=", "++", "..", "{", "}", "(", ")", "[", "]", ";", ":", ",", ".", "=", "<", ">", "+", "-", "*", "/", "?", "@", "|"];

    public static List<Token> Lex(string src)
    {
        var toks = new List<Token>();
        int i = 0, line = 1, col = 1;
        void Adv(int n) { for (var k = 0; k < n; k++) { if (src[i] == '\n') { line++; col = 1; } else col++; i++; } }
        while (i < src.Length)
        {
            var c = src[i];
            var pos = new Pos(line, col);
            if (char.IsWhiteSpace(c)) { Adv(1); continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                var e = src.IndexOf('\n', i); if (e < 0) e = src.Length;
                var text = src[i..e];
                if (text.StartsWith("///", StringComparison.Ordinal)) toks.Add(new Token(T.Doc, text[3..].TrimStart(' ').TrimEnd('\r'), pos));
                Adv(e - i); continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); var j = i + 1;
                while (true)
                {
                    if (j >= src.Length || src[j] == '\n') throw new SyntaxError("unterminated string", pos);
                    if (src[j] == '"') break;
                    if (src[j] == '\\' && j + 1 < src.Length)
                    {
                        j++;
                        switch (src[j])       // the escapes the printer writes (JSON)
                        {
                            case 'n': sb.Append('\n'); break; case 't': sb.Append('\t'); break; case 'r': sb.Append('\r'); break; case 'b': sb.Append('\b'); break; case 'f': sb.Append('\f'); break;
                            case 'u' when j + 4 < src.Length && int.TryParse(src.AsSpan(j + 1, 4), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var code): sb.Append((char)code); j += 4; break;
                            default: sb.Append(src[j]); break;
                        }
                    }
                    else sb.Append(src[j]);
                    j++;
                }
                toks.Add(new Token(T.Str, sb.ToString(), pos));
                Adv(j + 1 - i); continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var j = i;
                while (j < src.Length && char.IsAsciiDigit(src[j])) j++;
                // semver
                if (j + 1 < src.Length && src[j] == '.' && char.IsAsciiDigit(src[j + 1]))
                {
                    var k = j + 1; while (k < src.Length && char.IsAsciiDigit(src[k])) k++;
                    if (k + 1 < src.Length && src[k] == '.' && char.IsAsciiDigit(src[k + 1]))
                    {
                        var m = k + 1; while (m < src.Length && char.IsAsciiDigit(src[m])) m++;
                        toks.Add(new Token(T.Version, src[i..m], pos)); Adv(m - i); continue;
                    }
                    toks.Add(new Token(T.Dec, src[i..k], pos)); Adv(k - i); continue;
                }
                var unit = DurationUnit(src, j);
                if (unit > 0) { toks.Add(new Token(T.Duration, src[i..(j + unit)], pos)); Adv(j + unit - i); continue; }
                toks.Add(new Token(T.Int, src[i..j], pos)); Adv(j - i); continue;
            }
            if (c == '@' && i + 5 < src.Length && char.IsAsciiDigit(src[i + 1]) && src.IndexOf('T', i, Math.Min(12, src.Length - i)) > 0)
            {
                var j = i + 1; while (j < src.Length && !char.IsWhiteSpace(src[j]) && src[j] is not (',' or ';' or ')' or '}' or ']')) j++;
                toks.Add(new Token(T.Instant, src[(i + 1)..j], pos)); Adv(j - i); continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var j = i; while (j < src.Length && (char.IsAsciiLetterOrDigit(src[j]) || src[j] == '_')) j++;
                var word = src[i..j];
                if (word == "sha256" && j < src.Length && src[j] == ':')
                {
                    var k = j + 1; while (k < src.Length && (Uri.IsHexDigit(src[k]) || src[k] == '…')) k++;
                    toks.Add(new Token(T.Digest, src[i..k], pos)); Adv(k - i); continue;
                }
                toks.Add(new Token(T.Ident, word, pos)); Adv(j - i); continue;
            }
            var sym = Syms.FirstOrDefault(s => string.CompareOrdinal(src, i, s, 0, s.Length) == 0);
            if (sym is null) throw new SyntaxError($"unexpected character '{c}'", pos);
            toks.Add(new Token(T.Sym, sym, pos)); Adv(sym.Length);
        }
        toks.Add(new Token(T.Eof, "", new Pos(line, col)));
        return toks;
    }

    static int DurationUnit(string s, int j)
    {
        foreach (var u in new[] { "ms", "s", "m", "h" })
            if (string.CompareOrdinal(s, j, u, 0, u.Length) == 0 && (j + u.Length >= s.Length || !char.IsAsciiLetterOrDigit(s[j + u.Length]))) return u.Length;
        return 0;
    }

    public static long DurationNs(string text)
    {
        var n = text.TakeWhile(char.IsAsciiDigit).Count();
        var v = long.Parse(text[..n], CultureInfo.InvariantCulture);
        return text[n..] switch { "ms" => v * 1_000_000, "s" => v * 1_000_000_000, "m" => v * 60_000_000_000, _ => v * 3_600_000_000_000 };
    }
    public static string DurationText(long ns)
    {
        if (ns % 3_600_000_000_000 == 0) return ns / 3_600_000_000_000 + "h";
        if (ns % 60_000_000_000 == 0) return ns / 60_000_000_000 + "m";
        if (ns % 1_000_000_000 == 0) return ns / 1_000_000_000 + "s";
        return ns / 1_000_000 + "ms";
    }
}

// ---- AST --------------------------------------------------------------------------------------------------

public sealed record Ref(string? Alias, string Name, Pos Pos)
{
    public override string ToString() => Alias is null ? Name : Alias + "." + Name;
}
public sealed record Import(string Module, string Version, string? Digest, string Alias, Pos Pos, Pos DigestPos);
public sealed record VType(string Kind, string? Item, Ref? Target, Pos Pos);

/// <summary>What a relationship type says about its two ends, declared with `kind` and served with the ontology for a viewer to draw and filter by. The engine reads none of it. `communication`: the source was observed talking to the target. `composition`: one end is a part of the other or is carried by it (which end is the whole is the type's own matter). `dependency`: the source needs the target to work, so a failure of the target reaches the source. `support`: the source is a record about the target (evidence, a measurement, a rule) and no part of the modelled system.</summary>
public static class RelationshipKinds
{
    public static readonly string[] All = ["communication", "composition", "dependency", "support"];
}
public sealed record Member(string Name, string? Was, bool Optional, VType Type, bool Nullable, string? Unit, TeleMod? Tele, string? Doc, Pos Pos, List<(Value Value, string Role)>? Roles = null, string? TreeRole = null, bool Label = false);
public sealed record TeleMod(string Temporality, string Combine, long PeriodNs);

public abstract record Decl(string? Doc, Pos Pos);
public sealed record EntityDecl(bool Abstract, string Name, string? Was, List<Ref> Supers, List<Member> Members, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record RelDecl(bool Abstract, string Name, string? Was, Ref? Super, List<Ref> Sources, List<Ref> Targets, List<Member> Members, string? Doc, Pos Pos, bool Hierarchy = false, string? Kind = null, bool Rollup = false) : Decl(Doc, Pos);
public sealed record ExtendDecl(Ref Target, List<Member> Members, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record ConstraintDecl(string? Name, string Kind, Ref Target, string? Prop, string[] Values, decimal? Min, decimal? Max, string? Pattern, string? Side, long CardMin, long CardMax, string[] KeyProps, bool Warning, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record KeyComp(string Name, string Kind, string? Canonicalizer, Pos Pos);
public sealed record IdentityDecl(string Name, Ref? For, string Version, List<KeyComp> Components, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record EquivalenceDecl(string Name, Ref A, Ref B, string Mode, decimal Threshold, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record FieldDecl(string Name, bool Optional, VType Type, string? Unit, Pos Pos);
public sealed record RowsDecl(string Name, List<FieldDecl> Fields, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record SpaceSource(string Name, string Adapter, Ref Rows, Pos Pos);
public sealed record SpaceGraph(string Name, bool Master, Ref Projection, List<(string Port, string From, Value? Literal)> Args, Pos Pos);
public sealed record SpaceDecl(string Name, List<SpaceSource> Sources, List<SpaceGraph> Graphs, string? Doc, Pos Pos) : Decl(Doc, Pos);

public sealed record ModuleAst(string Name, string Version, string? Doc, List<Import> Imports, List<Decl> Decls, Pos Pos);

// ---- parser ------------------------------------------------------------------------------------------------

public sealed class Parser
{
    readonly List<Token> toks;
    int p;
    Parser(List<Token> toks) { this.toks = toks; }

    public static ModuleAst Parse(string source) => new Parser(Lexer.Lex(source)).File();


    Token Cur => toks[p];
    Token Peek(int n = 1) => toks[Math.Min(p + n, toks.Count - 1)];
    bool IsSym(string s) => Cur.Kind == T.Sym && Cur.Text == s;
    bool IsWord(string w) => Cur.Kind == T.Ident && Cur.Text == w;
    SyntaxError Err(string msg) => new(msg + $" (found '{(Cur.Kind == T.Eof ? "end of file" : Cur.Text)}')", Cur.Pos);
    Token Next() => toks[p++];
    Token Sym(string s) => IsSym(s) ? Next() : throw Err($"expected '{s}'");
    Token Word(string w) => IsWord(w) ? Next() : throw Err($"expected '{w}'");
    string Ident() => Cur.Kind == T.Ident ? Next().Text : throw Err("expected an identifier");
    bool TrySym(string s) { if (IsSym(s)) { p++; return true; } return false; }
    bool TryWord(string w) { if (IsWord(w)) { p++; return true; } return false; }
    string QName() { var s = Ident(); while (IsSym(".") && Peek().Kind == T.Ident) { p++; s += "." + Ident(); } return s; }
    string? Docs() { var d = new List<string>(); while (Cur.Kind == T.Doc) d.Add(Next().Text); return d.Count == 0 ? null : string.Join("\n", d); }
    Ref RefP()
    {
        var pos = Cur.Pos; var a = Ident();
        if (IsSym(".") && Peek().Kind == T.Ident) { p++; return new Ref(a, Ident(), pos); }
        return new Ref(null, a, pos);
    }

    ModuleAst File()
    {
        var doc = Docs();
        var pos = Cur.Pos;
        Word("module");
        var name = QName();
        var version = "1.0.0";
        if (TrySym("@")) version = Cur.Kind == T.Version ? Next().Text : throw Err("expected a version");
        Sym(";");
        var imports = new List<Import>();
        while (IsWord("import")) imports.Add(Import());
        var decls = new List<Decl>();
        while (Cur.Kind != T.Eof) decls.Add(Declaration());
        return new ModuleAst(name, version, doc, imports, decls, pos);
    }

    Import Import()
    {
        var pos = Next().Pos;
        var m = QName();
        Sym("@");
        var v = Cur.Kind == T.Version ? Next().Text : throw Err("expected a version");
        var dpos = Cur.Pos;
        string? digest = null;
        if (Cur.Kind == T.Digest) { var d = Next().Text; digest = d.EndsWith('…') || d.Length != 71 ? null : d; }
        else throw Err("expected a sha256 digest");
        Word("as");
        var alias = Ident();
        Sym(";");
        return new Import(m, v, digest, alias, pos, dpos);
    }

    Decl Declaration()
    {
        var doc = Docs();
        var pos = Cur.Pos;
        var abs = TryWord("abstract");
        if (!abs && Cur.Kind != T.Ident) throw Err("expected a declaration");
        var kw = Ident();
        switch (kw)
        {
            case "entity":
                {
                    var name = Ident(); var was = TryWord("was") ? Ident() : null;
                    var supers = new List<Ref>();
                    if (TrySym(":")) { supers.Add(RefP()); while (TrySym(",")) supers.Add(RefP()); }
                    return new EntityDecl(abs, name, was, supers, MembersOrSemi(), doc, pos);
                }
            case "relationship":
                {
                    var name = Ident(); var was = TryWord("was") ? Ident() : null;
                    Ref? sup = TrySym(":") ? RefP() : null;
                    Sym("("); var src = RefAlt(); Sym(")"); Sym("->"); Sym("("); var tgt = RefAlt(); Sym(")");
                    var hierarchy = TryWord("hierarchy"); var rollup = TryWord("rollup");
                    var kind = TryWord("kind") ? (Cur.Kind == T.Ident && RelationshipKinds.All.Contains(Cur.Text) ? Next().Text : throw Err("expected a relationship kind: " + string.Join(", ", RelationshipKinds.All))) : null;
                    return new RelDecl(abs, name, was, sup, src, tgt, MembersOrSemi(), doc, pos, hierarchy, kind, rollup);
                }
            case "extend": { var t = RefP(); return new ExtendDecl(t, Members(), doc, pos); }
            case "constraint": return Constraint(doc, pos);
            case "identity": return Identity(doc, pos);
            case "equivalence":
                {
                    var name = Ident(); Word("between"); var a = RefP(); Sym(","); var b = RefP();
                    var mode = Cur.Kind == T.Ident && Cur.Text is "assert" or "review" ? Next().Text : throw Err("expected assert or review");
                    Word("threshold"); var th = Number(); Sym(";");
                    return new EquivalenceDecl(name, a, b, mode, th, doc, pos);
                }
            case "rows":
                {
                    var name = Ident(); Sym("{");
                    var fields = new List<FieldDecl>();
                    while (!IsSym("}"))
                    {
                        var fpos = Cur.Pos; var f = Ident(); var opt = TrySym("?"); Sym(":"); var vt = VTypeP();
                        string? unit = TryWord("unit") ? Str() : null; Sym(";");
                        fields.Add(new FieldDecl(f, opt, vt, unit, fpos));
                    }
                    Sym("}");
                    return new RowsDecl(name, fields, doc, pos);
                }
            case "projection": p--; throw new SyntaxError("a projection is written in a file of its own beside the module, named module.projection with the extension of its language", Cur.Pos);
            case "space": return Space(doc, pos);
            default: p--; throw Err("expected entity, relationship, extend, constraint, identity, equivalence, rows or space");
        }
    }

    List<Ref> RefAlt() { var l = new List<Ref> { RefP() }; while (TrySym("|")) l.Add(RefP()); return l; }
    List<Member> MembersOrSemi() => TrySym(";") ? [] : Members();
    string Str() => Cur.Kind == T.Str ? Next().Text : throw Err("expected a string");

    decimal Number()
    {
        var neg = TrySym("-");
        if (Cur.Kind is not (T.Int or T.Dec)) throw Err("expected a number");
        var v = decimal.Parse(Next().Text, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }
    long Star() { p++; return -1; }
    long Int() => Cur.Kind == T.Int ? long.Parse(Next().Text, CultureInfo.InvariantCulture) : throw Err("expected an integer");

    VType VTypeP()
    {
        var pos = Cur.Pos; var k = Ident();
        switch (k)
        {
            case "Text" or "Integer" or "Decimal" or "Boolean" or "Instant": return new VType(k, null, null, pos);
            case "List": { Sym("<"); var i = Ident(); Sym(">"); return new VType("List", i, null, pos); }
            case "Ref": { Sym("<"); var r = RefP(); Sym(">"); return new VType("Entity", null, r, pos); }
            default: p--; throw Err("expected a value type (Text, Integer, Decimal, Boolean, Instant, List<..>, Ref<..>)");
        }
    }

    List<Member> Members()
    {
        Sym("{");
        var l = new List<Member>();
        while (!IsSym("}"))
        {
            var doc = Docs();
            var pos = Cur.Pos; var name = Ident(); var was = TryWord("was") ? Ident() : null; var opt = TrySym("?"); Sym(":");
            var vt = VTypeP(); var nullable = false; string? unit = null; TeleMod? tele = null; List<(Value, string)>? roles = null; string? treeRole = null; var label = false;
            while (true)
            {
                if (TryWord("roles"))
                {
                    roles = [];
                    Sym("{");
                    do
                    {
                        Value v;
                        if (Cur.Kind == T.Str) v = Value.Text(Next().Text);
                        else if (Cur.Kind == T.Int) v = Value.Integer(System.Numerics.BigInteger.Parse(Next().Text, CultureInfo.InvariantCulture));
                        else if (Cur.Kind == T.Dec) v = Value.Decimal(decimal.Parse(Next().Text, CultureInfo.InvariantCulture));
                        else if (IsWord("true") || IsWord("false")) v = Value.Boolean(Next().Text == "true");
                        else throw Err("expected a string, number or boolean value");
                        Sym(":");
                        var role = Cur.Kind == T.Ident && Cur.Text is "ok" or "warning" or "danger" or "unknown" or "neutral" ? Next().Text : throw Err("expected ok, warning, danger, unknown or neutral");
                        roles.Add((v, role));
                    } while (TrySym(","));
                    Sym("}");
                }
                else
                if (TryWord("treerole")) treeRole = Cur.Kind == T.Ident && Cur.Text is "own" or "rolledUp" or "severity" or "kind" or "healthy" or "degraded" or "unhealthy" or "unknown" or "curatedBy" or "critical" or "hidden" or "mode" or "share" ? Next().Text : throw Err("expected own, rolledUp, severity, kind, healthy, degraded, unhealthy, unknown, curatedBy, critical, hidden, mode or share");
                else if (TryWord("nullable")) nullable = true;
                else if (TryWord("label")) label = true;
                else if (TryWord("unit")) unit = Str();
                else if (TryWord("telemetry"))
                {
                    var temp = Ident(); var comb = Ident(); Word("every");
                    if (Cur.Kind != T.Duration) throw Err("expected a duration");
                    tele = new TeleMod(temp, comb, Lexer.DurationNs(Next().Text));
                }
                else break;
            }
            Sym(";");
            l.Add(new Member(name, was, opt, vt, nullable, unit, tele, doc, pos, roles, treeRole, label));
        }
        Sym("}");
        return l;
    }

    Decl Constraint(string? doc, Pos pos)
    {
        string? name = null;
        if (Cur.Kind == T.Ident && Cur.Text is not ("key" or "enum" or "range" or "pattern" or "cardinality" or "acyclic")) name = Next().Text;
        var kind = Ident();
        Ref target; string? prop = null; string[] values = []; decimal? min = null, max = null; string? pattern = null, side = null; long cmin = 0, cmax = 0; string[] keys = [];
        switch (kind)
        {
            case "key":
                target = RefP(); Sym("("); var ks = new List<string> { Ident() }; while (TrySym(",")) ks.Add(Ident()); Sym(")"); keys = [.. ks]; break;
            case "enum":
                { var m = Member2(); target = m.Item1; prop = m.Item2; Word("in"); Sym("["); var vs = new List<string> { Str() }; while (TrySym(",")) vs.Add(Str()); Sym("]"); values = [.. vs]; break; }
            case "range":
                { var m = Member2(); target = m.Item1; prop = m.Item2; if (Cur.Kind is T.Int or T.Dec || IsSym("-")) min = Number(); Sym(".."); if (Cur.Kind is T.Int or T.Dec || IsSym("-")) max = Number(); break; }
            case "pattern":
                { var m = Member2(); target = m.Item1; prop = m.Item2; pattern = Str(); break; }
            case "cardinality":
                target = RefP(); Word("per"); side = Ident(); cmin = Int(); Sym(".."); cmax = IsSym("*") ? Star() : Int(); break;
            case "acyclic": target = RefP(); break;
            default: p--; throw Err("expected a constraint kind (key, enum, range, pattern, cardinality, acyclic)");
        }
        var warn = TryWord("warning");
        Sym(";");
        return new ConstraintDecl(name, kind, target, prop, values, min, max, pattern, side, cmin, cmax, keys, warn, doc, pos);
    }

    (Ref, string) Member2()
    {
        var pos = Cur.Pos; var a = Ident(); string? b = null; string? c = null;
        if (TrySym(".")) { b = Ident(); if (TrySym(".")) c = Ident(); }
        if (c is not null) return (new Ref(a, b!, pos), c);
        if (b is not null) return (new Ref(null, a, pos), b);
        throw Err("expected Type.property");
    }

    Decl Identity(string? doc, Pos pos)
    {
        var name = Ident(); Ref? @for = TryWord("for") ? RefP() : null;
        Word("version"); var ver = Str(); Sym("{");
        var comps = new List<KeyComp>();
        while (!IsSym("}"))
        {
            var cpos = Cur.Pos; var c = Ident(); Sym(":"); var k = Ident(); var by = TryWord("by") ? Ident() : null; Sym(";");
            comps.Add(new KeyComp(c, k, by, cpos));
        }
        Sym("}");
        return new IdentityDecl(name, @for, ver, comps, doc, pos);
    }

    Decl Space(string? doc, Pos pos)
    {
        var name = Ident(); Sym("{");
        var sources = new List<SpaceSource>(); var graphs = new List<SpaceGraph>();
        while (!IsSym("}"))
        {
            var ipos = Cur.Pos;
            if (TryWord("source"))
            {
                var n = Ident(); Word("adapter"); var a = Str(); Word("rows"); var r = RefP(); Sym(";");
                sources.Add(new SpaceSource(n, a, r, ipos));
            }
            else
            {
                var master = TryWord("master"); Word("graph"); var n = Ident(); Sym("="); var proj = RefP(); Sym("(");
                var args = new List<(string, string, Value?)>();
                do
                {
                    var port = Ident(); Sym(":");
                    if (Cur.Kind == T.Str) args.Add((port, "", Value.Text(Next().Text)));
                    else if (Cur.Kind == T.Int) args.Add((port, "", Value.Integer(System.Numerics.BigInteger.Parse(Next().Text, CultureInfo.InvariantCulture))));
                    else if (Cur.Kind == T.Dec) args.Add((port, "", Value.Decimal(decimal.Parse(Next().Text, CultureInfo.InvariantCulture))));
                    else if (IsWord("true") || IsWord("false")) args.Add((port, "", Value.Boolean(Next().Text == "true")));
                    else args.Add((port, Ident(), null));
                } while (TrySym(","));
                Sym(")"); Sym(";");
                graphs.Add(new SpaceGraph(n, master, proj, args, ipos));
            }
        }
        Sym("}");
        return new SpaceDecl(name, sources, graphs, doc, pos);
    }
}

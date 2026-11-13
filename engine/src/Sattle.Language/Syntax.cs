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
public sealed record RelDecl(bool Abstract, string Name, string? Was, Ref? Super, List<Ref> Sources, List<Ref> Targets, List<Member> Members, string? Doc, Pos Pos, bool Hierarchy = false, string? Kind = null) : Decl(Doc, Pos);
public sealed record ExtendDecl(Ref Target, List<Member> Members, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record ConstraintDecl(string? Name, string Kind, Ref Target, string? Prop, string[] Values, decimal? Min, decimal? Max, string? Pattern, string? Side, long CardMin, long CardMax, string[] KeyProps, bool Warning, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record KeyComp(string Name, string Kind, string? Canonicalizer, Pos Pos);
public sealed record IdentityDecl(string Name, Ref? For, string Version, List<KeyComp> Components, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record EquivalenceDecl(string Name, Ref A, Ref B, string Mode, decimal Threshold, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record FieldDecl(string Name, bool Optional, VType Type, string? Unit, Pos Pos);
public sealed record RowsDecl(string Name, List<FieldDecl> Fields, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record PortDecl(string Name, string Kind, Ref Target, Pos Pos, Value? Default = null);
public sealed record ProjectionDecl(string Name, string Purpose, bool Forward, string Output, List<PortDecl> Ports, long? Retain, List<ProjItem> Items, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record SpaceSource(string Name, string Adapter, Ref Rows, Pos Pos);
public sealed record SpaceGraph(string Name, bool Master, Ref Projection, List<(string Port, string From, Value? Literal)> Args, Pos Pos);
public sealed record SpaceDecl(string Name, List<SpaceSource> Sources, List<SpaceGraph> Graphs, string? Doc, Pos Pos) : Decl(Doc, Pos);

public abstract record ProjItem(Pos Pos);
public sealed record Policies(List<string> Accept, Ref? CreateKey, List<(string Name, Expr Value, Pos Pos)> CreateDefault, string? OnDelete, List<(string Name, Expr Value, Pos Pos)> DeleteSet, Pos Pos);
public sealed record RuleItem(string Name, string Class, bool PutPut, string? Reason, List<Stmt> Stmts, Pos Pos, Policies? Policy = null) : ProjItem(Pos);
public abstract record Stmt(Pos Pos) : ProjItem(Pos);
public sealed record LetStmt(string Name, Pipeline Pipe, Pos Pos) : Stmt(Pos);
public sealed record EmitEntityStmt(bool Update, Ref Type, string From, Expr Id, List<(string Name, Expr Value, Pos Pos)> Set, Pos Pos) : Stmt(Pos);
public sealed record EmitRelStmt(bool Update, Ref Type, string From, string KeyName, Expr Source, Expr Target, List<(string Name, Expr Value, Pos Pos)> Set, string? Retype, Pos Pos) : Stmt(Pos);
/// <summary>Defaults of the lift statement (README, "Edge lifting").</summary>
public static class LiftDefaults { public const long WithinNs = 24L * 3600 * 1_000_000_000; }
public sealed record LiftStateDecl(string Target, string Of, decimal Above, long EnterAfter, string High, decimal Below, long ExitAfter, string Low, string Initial, Pos Pos);
/// <summary>`lift NAME as REL from GRAPH EDGETYPE via GRAPH MAPTYPE [carry p = q, ...] [keep c = src.p, ...] [when image.p == v] [derive p = expr, ...] [state p of m above A for N then "x" below B for M then "y" initial "z"] [images reject|each] [selfloops keep|drop] [within 24h];` The edges of the lower graph lifted along the mapping relationship to the relationship REL of this projection (README, "Edge lifting").</summary>
public sealed record LiftStmt(string Name, Ref Emit, string EdgesGraph, Ref EdgeType, string MapGraph, Ref MapType, List<(string Target, string Source, Pos Pos)> Carry, List<(string Name, Expr Value, Pos Pos)> Derive, List<(string Name, string Path, Pos Pos)> Keep, Expr? When, List<LiftStateDecl> States, string? Images, string SelfLoops, long WithinNs, Pos Pos) : Stmt(Pos);
public sealed record LinkStmt(Ref Policy, string From, Expr Left, Expr Right, Expr Confidence, Pos Pos) : Stmt(Pos);

public sealed record Pipeline(PipeSource Source, List<Step> Steps, Pos Pos);
public abstract record PipeSource(Pos Pos);
public sealed record IdentSource(string Name, Pos Pos) : PipeSource(Pos);
public sealed record MatchSource(string Graph, List<PathPat> Paths, Pos Pos) : PipeSource(Pos);
public sealed record ValuesSource(List<List<(string Name, Expr Value)>> Rows, Pos Pos) : PipeSource(Pos);
public sealed record FixpointSource(string Seed, Pipeline Step, long Max, Pos Pos) : PipeSource(Pos);
public sealed record JoinSource(string A, string B, Expr On, Pos Pos) : PipeSource(Pos);
public sealed record UnionSource(string A, string B, string Conflict, Pos Pos) : PipeSource(Pos);
public sealed record PathPat(List<NodePat> Nodes, List<EdgePat> Edges);
public sealed record NodePat(string Var, Ref? Type, Pos Pos);
public sealed record EdgePat(string? Var, Ref Type, bool Forward, Pos Pos);

public abstract record Step(Pos Pos);
public sealed record WhereStep(Expr Pred, Pos Pos) : Step(Pos);
public sealed record DistinctStep(Pos Pos) : Step(Pos);
public sealed record TraverseStep(string Graph, Ref Rel, string Direction, long Min, long Max, string From, string As, Pos Pos) : Step(Pos);
public sealed record SelectStep(List<(string Name, Expr Value)> Record, Pos Pos) : Step(Pos);
public sealed record AggDecl(string Target, string Fn, Expr? Value, Expr? Weight, Pos Pos);
public sealed record GroupStep(List<(string Name, Expr Value)> Keys, List<AggDecl> Aggs, Pos Pos) : Step(Pos);
public sealed record WindowStep(string On, long WidthNs, string Late, Pos Pos) : Step(Pos);
public sealed record RetainStep(string On, long AgeNs, Pos Pos) : Step(Pos);
/// <summary>`Fills[i]`: the fill value of path i, a constant for a covered window without a record (Def. 7.15). `TtlNs`: the time to live after which an element without a record is in no data (Def. 7.16).</summary>
public sealed record SeriesStep(string Graph, List<string> Paths, long WithinNs, Pos Pos, List<decimal?>? Fills = null, long? TtlNs = null) : Step(Pos);
/// <summary>`NoDataAny`: the key is in no data when one member is; otherwise when all are (Def. 7.16).</summary>
public sealed record RollupStep(List<(string Name, Expr Value)> Keys, List<AggDecl> Aggs, Pos Pos, bool NoDataAny = false) : Step(Pos);
public sealed record ThresholdStep(Expr Value, List<(string Name, Expr Value)> Keys, decimal Above, long EnterAfter, string High, decimal Below, long ExitAfter, string Low, string Initial, string As, Pos Pos, Expr? AboveE = null, Expr? EnterE = null, Expr? BelowE = null, Expr? ExitE = null, string? NoData = null, bool NoDataIgnore = false) : Step(Pos);

public abstract record Expr(Pos Pos);
public sealed record LitExpr(Value Value, Pos Pos) : Expr(Pos);
public sealed record PathExpr(string Path, Pos Pos) : Expr(Pos);
public sealed record CallExpr(string Fn, List<Expr> Args, Pos Pos) : Expr(Pos);
public sealed record IdExpr(Ref Domain, List<(string Name, Expr Value, Pos Pos)> Components, Pos Pos) : Expr(Pos);

public sealed record ModuleAst(string Name, string Version, string? Doc, List<Import> Imports, List<Decl> Decls, Pos Pos);

// ---- parser ------------------------------------------------------------------------------------------------

public sealed class Parser
{
    readonly List<Token> toks;
    int p;
    Parser(List<Token> toks) { this.toks = toks; }

    public static ModuleAst Parse(string source) => new Parser(Lexer.Lex(source)).File();

    /// <summary>The deepest nesting of expressions and pipelines a text may have. Texts written by people nest a handful of levels; without a limit a text of a few hundred nested brackets overflows the stack, which ends the process.</summary>
    public const int MaxNesting = 100;
    int depth;
    void Enter() { if (++depth > MaxNesting) throw new SyntaxError($"nested deeper than {MaxNesting} levels", Cur.Pos); }

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
    SyntaxError Unsupported(string what) => new($"{what} is not supported in this engine slice (scope: research 03 examples 1 and 2)", Cur.Pos);

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
                    var hierarchy = TryWord("hierarchy");
                    var kind = TryWord("kind") ? (Cur.Kind == T.Ident && RelationshipKinds.All.Contains(Cur.Text) ? Next().Text : throw Err("expected a relationship kind: " + string.Join(", ", RelationshipKinds.All))) : null;
                    return new RelDecl(abs, name, was, sup, src, tgt, MembersOrSemi(), doc, pos, hierarchy, kind);
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
            case "projection": return Projection(doc, pos);
            case "space": return Space(doc, pos);
            default: p--; throw Err("expected entity, relationship, extend, constraint, identity, equivalence, rows, projection or space");
        }
    }

    List<Ref> RefAlt() { var l = new List<Ref> { RefP() }; while (TrySym("|")) l.Add(RefP()); return l; }
    List<Member> MembersOrSemi() => TrySym(";") ? [] : Members();
    string Str() => Cur.Kind == T.Str ? Next().Text : throw Err("expected a string");
    /// <summary>A number, or an expression over the columns of the row (a threshold read from data).</summary>
    (decimal N, Expr? E) NumOrExpr()
    {
        var save = p;
        if (Cur.Kind is T.Int or T.Dec || (IsSym("-") && p + 1 < toks.Count && toks[p + 1].Kind is T.Int or T.Dec)) { var n = Number(); if (IsWord("for") || IsWord("below") || IsWord("then")) return (n, null); p = save; }
        return (0, Expression());
    }

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

    Decl Projection(string? doc, Pos pos)
    {
        var name = Ident();
        var purpose = Cur.Kind == T.Ident && Cur.Text is "ingestion" or "view" or "migration" or "verification" ? Next().Text : throw Err("expected a purpose (ingestion, view, migration, verification)");
        var fwd = TryWord("forward"); Word("output"); var output = QName(); Sym("{");
        var ports = new List<PortDecl>(); long? retain = null; var items = new List<ProjItem>();
        while (!IsSym("}"))
        {
            if (IsWord("in") ) { var ppos = Next().Pos; var pn = Ident(); Sym(":"); var pk = Ident(); if (pk is not ("rows" or "graph" or "param")) throw Err("expected rows, graph or param"); var r = RefP(); Value? def = null;
                if (pk == "param" && TrySym("="))
                {
                    if (Cur.Kind == T.Str) def = Value.Text(Next().Text);
                    else if (Cur.Kind == T.Int) def = Value.Integer(System.Numerics.BigInteger.Parse(Next().Text, CultureInfo.InvariantCulture));
                    else if (Cur.Kind == T.Dec) def = Value.Decimal(decimal.Parse(Next().Text, CultureInfo.InvariantCulture));
                    else if (IsWord("true") || IsWord("false")) def = Value.Boolean(Next().Text == "true");
                    else throw Err("expected a constant");
                }
                Sym(";"); ports.Add(new PortDecl(pn, pk, r, ppos, def)); }
            else if (IsWord("retain")) { p++; retain = Int(); Sym(";"); }
            else if (IsWord("rule")) items.Add(Rule());
            else items.Add(Stmt());
        }
        Sym("}");
        return new ProjectionDecl(name, purpose, fwd, output, ports, retain, items, doc, pos);
    }

    RuleItem Rule()
    {
        var pos = Next().Pos; var name = Ident();
        var cls = Cur.Kind == T.Ident && Cur.Text is "bijective" or "lens" or "sync" or "readonly" ? Next().Text : throw Err("expected a rule class (bijective, lens, sync, readonly)");
        var pp = cls == "lens" && TryWord("putput");
        string? reason = Cur.Kind == T.Str ? Next().Text : null;
        Sym("{");
        var stmts = new List<Stmt>();
        var accept = new List<string>(); Ref? createKey = null; var cdef = new List<(string, Expr, Pos)>(); string? onDelete = null; var dset = new List<(string, Expr, Pos)>(); Pos? ppos = null;
        while (!IsSym("}"))
        {
            if (IsWord("source") || IsWord("target") || IsWord("correspond") || IsWord("equate") || IsWord("keep")) throw Unsupported($"rule item '{Cur.Text}' (sync rules are later work)");
            if (IsWord("accept"))
            {
                ppos ??= Cur.Pos; p++;
                do
                {
                    var e = Ident();
                    if (e == "update") { Sym("("); var ps = new List<string> { Ident() }; while (TrySym(",")) ps.Add(Ident()); Sym(")"); accept.Add("update(" + string.Join(",", ps) + ")"); }
                    else if (e is "create" or "delete") accept.Add(e);
                    else { p--; throw Err("expected create, delete or update(...)"); }
                } while (TrySym(","));
                Sym(";");
            }
            else if (IsWord("on"))
            {
                ppos ??= Cur.Pos; p++;
                if (TryWord("create")) { Word("key"); createKey = RefP(); if (TryWord("default")) cdef = RecordP(); Sym(";"); }
                else if (TryWord("delete"))
                {
                    if (TryWord("remove")) onDelete = "remove";
                    else { Word("set"); onDelete = "set"; dset = RecordP(); }
                    Sym(";");
                }
                else throw Err("expected create or delete");
            }
            else stmts.Add(Stmt());
        }
        Sym("}");
        var pol = ppos is null ? null : new Policies(accept, createKey, cdef, onDelete, dset, ppos.Value);
        return new RuleItem(name, cls, pp, reason, stmts, pos, pol);
    }

    Stmt Stmt()
    {
        var pos = Cur.Pos;
        if (TryWord("let"))
        {
            var name = Ident(); Sym("="); var pipe = Pipe(); Sym(";");
            return new LetStmt(name, pipe, pos);
        }
        if (TryWord("emit"))
        {
            var upd = TryWord("update");
            if (TryWord("entity"))
            {
                var t = RefP(); Word("from"); var from = Ident(); Word("identity"); var id = Expression();
                var set = TryWord("set") ? RecordP() : [];
                Sym(";");
                return new EmitEntityStmt(upd, t, from, id, set, pos);
            }
            if (TryWord("relationship"))
            {
                var t = RefP(); Word("from"); var from = Ident(); Word("key"); var key = Ident(); Sym(":");
                var src = Expression(); Sym("->"); var tgt = Expression();
                var set = TryWord("set") ? RecordP() : [];
                string? retype = null;
                if (TryWord("on")) { Word("retype"); retype = Ident(); }
                Sym(";");
                return new EmitRelStmt(upd, t, from, key, src, tgt, set, retype, pos);
            }
            throw Err("expected entity or relationship");
        }
        if (TryWord("lift"))
        {
            var name = Ident(); Word("as"); var emit = RefP();
            Word("from"); var eg = Ident(); var et = RefP(); Word("via"); var mg = Ident(); var mt = RefP();
            var carry = new List<(string, string, Pos)>(); var derive = new List<(string, Expr, Pos)>(); var keep = new List<(string, string, Pos)>(); Expr? when = null; var states = new List<LiftStateDecl>();
            string? images = null; string selfLoops = "drop"; long within = LiftDefaults.WithinNs;
            while (!IsSym(";"))
            {
                var cp = Cur.Pos;
                if (TryWord("carry")) { do { var cpos = Cur.Pos; var t = Ident(); Sym("="); carry.Add((t, Ident(), cpos)); } while (TrySym(",")); }
                else if (TryWord("when")) { when = Expression(); }
                else if (TryWord("keep")) { do { var kpos = Cur.Pos; var t = Ident(); Sym("="); keep.Add((t, PathText(), kpos)); } while (TrySym(",")); }
                else if (TryWord("derive")) { do { var dpos = Cur.Pos; var t = Ident(); Sym("="); derive.Add((t, Expression(), dpos)); } while (TrySym(",")); }
                else if (TryWord("state"))
                {
                    var t = Ident(); Word("of"); var of = Ident();
                    Word("above"); var above = Number(); Word("for"); var en = Int(); Word("then"); var high = Str();
                    Word("below"); var below = Number(); Word("for"); var ex = Int(); Word("then"); var low = Str(); Word("initial"); var init = Str();
                    states.Add(new LiftStateDecl(t, of, above, en, high, below, ex, low, init, cp));
                }
                else if (TryWord("images")) { images = Cur.Kind == T.Ident && Cur.Text is "reject" or "each" ? Next().Text : throw Err("expected reject or each"); }
                else if (TryWord("selfloops")) { selfLoops = Cur.Kind == T.Ident && Cur.Text is "keep" or "drop" ? Next().Text : throw Err("expected keep or drop"); }
                else if (TryWord("within")) { if (Cur.Kind != T.Duration) throw Err("expected a duration"); within = Lexer.DurationNs(Next().Text); }
                else throw Err("expected carry, keep, when, derive, state, images, selfloops, within or ';'");
            }
            Sym(";");
            return new LiftStmt(name, emit, eg, et, mg, mt, carry, derive, keep, when, states, images, selfLoops, within, pos);
        }
        if (TryWord("link"))
        {
            var pol = RefP(); Word("from"); var from = Ident(); Word("left"); var l = Expression(); Word("right"); var r = Expression(); Word("confidence"); var c = Expression(); Sym(";");
            return new LinkStmt(pol, from, l, r, c, pos);
        }
        throw Err("expected let, emit, lift, link or rule");
    }

    List<(string, Expr, Pos)> RecordP()
    {
        Sym("{");
        var l = new List<(string, Expr, Pos)>();
        do { var pos = Cur.Pos; var n = Ident(); Sym(":"); l.Add((n, Expression(), pos)); } while (TrySym(","));
        Sym("}");
        return l;
    }
    List<(string Name, Expr Value)> Rec() => RecordP().Select(x => (x.Item1, x.Item2)).ToList();

    Pipeline Pipe() { Enter(); try { return PipeBody(); } finally { depth--; } }

    Pipeline PipeBody()
    {
        var pos = Cur.Pos;
        PipeSource src;
        if (IsWord("match"))
        {
            var mp = Next().Pos; var g = Ident(); var paths = new List<PathPat> { PathPattern() };
            while (TrySym(",")) paths.Add(PathPattern());
            src = new MatchSource(g, paths, mp);
        }
        else if (IsWord("union"))
        {
            var up = Next().Pos; var a = Ident(); Sym(","); var b = Ident();
            var conflict = TryWord("conflict") ? (Cur.Kind == T.Ident && Cur.Text is "requireEqual" or "preferFirst" or "preferLast" or "preserveEvidence" ? Next().Text : throw Err("expected requireEqual, preferFirst, preferLast or preserveEvidence")) : "preserveEvidence";
            src = new UnionSource(a, b, conflict, up);
        }
        else if (IsWord("join"))
        {
            var jp = Next().Pos; var a = Ident(); Sym(","); var b = Ident(); Word("on");
            src = new JoinSource(a, b, Expression(), jp);
        }
        else if (IsWord("fixpoint"))
        {
            var fp = Next().Pos; var seed = Ident(); Word("via"); var step = Pipe(); Word("max");
            src = new FixpointSource(seed, step, Int(), fp);
        }
        else if (IsWord("values"))
        {
            var vp = Next().Pos; var rows = new List<List<(string, Expr)>> { Rec() };
            while (TrySym(",")) rows.Add(Rec());
            src = new ValuesSource(rows, vp);
        }
        else src = new IdentSource(Ident(), pos);
        var steps = new List<Step>();
        while (TrySym("|>")) steps.Add(StepP());
        return new Pipeline(src, steps, pos);
    }

    PathPat PathPattern()
    {
        var nodes = new List<NodePat> { NodePattern() }; var edges = new List<EdgePat>();
        while (IsSym("-[") || IsSym("<-["))
        {
            var fwd = Cur.Text == "-["; var pos = Next().Pos;
            string? v = null;
            if (Cur.Kind == T.Ident && Peek().Kind == T.Sym && Peek().Text == ":") { v = Ident(); }
            Sym(":"); var t = RefP();
            if (fwd) Sym("]->"); else Sym("]-");
            if (IsSym("{")) throw Unsupported("quantified path patterns");
            edges.Add(new EdgePat(v, t, fwd, pos));
            nodes.Add(NodePattern());
        }
        return new PathPat(nodes, edges);
    }

    NodePat NodePattern()
    {
        var pos = Sym("(").Pos; var v = Ident(); Ref? t = TrySym(":") ? RefP() : null; Sym(")");
        return new NodePat(v, t, pos);
    }

    Step StepP()
    {
        var pos = Cur.Pos;
        if (TryWord("where")) return new WhereStep(Expression(), pos);
        if (TryWord("select")) return new SelectStep(Rec(), pos);
        if (TryWord("group")) { Word("by"); var k = Rec(); Word("aggregate"); return new GroupStep(k, AggList(), pos); }
        if (TryWord("window"))
        {
            Word("on"); var on = Ident(); Word("width");
            if (Cur.Kind != T.Duration) throw Err("expected a duration");
            var w = Lexer.DurationNs(Next().Text); Word("late");
            var late = Cur.Kind == T.Ident && Cur.Text is "ignore" or "revise" or "reject" ? Next().Text : throw Err("expected ignore, revise or reject");
            return new WindowStep(on, w, late, pos);
        }
        if (TryWord("retain"))
        {
            Word("on"); var on = Ident(); Word("for");
            if (Cur.Kind != T.Duration) throw Err("expected a duration");
            return new RetainStep(on, Lexer.DurationNs(Next().Text), pos);
        }
        if (TryWord("series"))
        {
            var g = Ident(); var paths = new List<string>(); var fills = new List<decimal?>();
            do { paths.Add(PathText()); fills.Add(TryWord("fill") ? Number() : null); } while (TrySym(","));
            Word("within");
            if (Cur.Kind != T.Duration) throw Err("expected a duration");
            var within = Lexer.DurationNs(Next().Text);
            long? ttl = null;
            if (TryWord("ttl")) { if (Cur.Kind != T.Duration) throw Err("expected a duration"); ttl = Lexer.DurationNs(Next().Text); }
            return new SeriesStep(g, paths, within, pos, fills, ttl);
        }
        if (TryWord("rollup")) { Word("by"); var k = Rec(); Word("combine"); var aggs = AggList(); var any = TryWord("nodata"); if (any) Word("any"); return new RollupStep(k, aggs, pos, any); }
        if (TryWord("threshold"))
        {
            var v = Expression(); Word("by"); var k = Rec();
            Word("above"); var (above, aboveE) = NumOrExpr(); Word("for"); var (en, enE) = NumOrExpr(); Word("then"); var high = Str();
            Word("below"); var (below, belowE) = NumOrExpr(); Word("for"); var (ex, exE) = NumOrExpr(); Word("then"); var low = Str();
            Word("initial"); var init = Str();
            string? noData = null; var ignore = false;
            if (TryWord("nodata")) { if (TryWord("then")) noData = Str(); else { Word("ignore"); ignore = true; } }
            Word("as"); var asn = Ident();
            return new ThresholdStep(v, k, above, (long)en, high, below, (long)ex, low, init, asn, pos, aboveE, enE, belowE, exE, noData, ignore);
        }
        if (TryWord("distinct")) return new DistinctStep(pos);
        if (TryWord("traverse"))
        {
            var g = Ident(); Word("via"); var rel = RefP();
            var dir = Cur.Kind == T.Ident && Cur.Text is "out" or "in" or "either" ? Next().Text : throw Err("expected out, in or either");
            Word("depth"); var min = Int(); Sym(".."); var max = Int(); Word("from"); var from = Ident(); Word("as"); var asn = Ident();
            return new TraverseStep(g, rel, dir, min, max, from, asn, pos);
        }
        if (IsWord("delta")) throw Unsupported($"step '{Cur.Text}'");
        throw Err("expected a pipeline step");
    }

    string PathText() { var s = Ident(); while (TrySym(".")) s += "." + Ident(); return s; }

    List<AggDecl> AggList()
    {
        Sym("{");
        var l = new List<AggDecl>();
        do
        {
            var pos = Cur.Pos; var t = Ident(); Sym(":"); var fn = Ident(); Sym("(");
            Expr? v = null, w = null;
            if (!IsSym(")")) { v = Expression(); if (TrySym(",")) w = Expression(); }
            Sym(")");
            l.Add(new AggDecl(t, fn, v, w, pos));
        } while (TrySym(","));
        Sym("}");
        return l;
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

    // ---- expressions ----
    public Expr Expression() { Enter(); try { return Disj(); } finally { depth--; } }
    Expr Disj() { var l = Conj(); if (!IsWord("or")) return l; var args = new List<Expr> { l }; var pos = l.Pos; while (TryWord("or")) args.Add(Conj()); return new CallExpr("Or", args, pos); }
    Expr Conj() { var l = Neg(); if (!IsWord("and")) return l; var args = new List<Expr> { l }; var pos = l.Pos; while (TryWord("and")) args.Add(Neg()); return new CallExpr("And", args, pos); }
    Expr Neg() { var pos = Cur.Pos; if (TryWord("not")) return new CallExpr("Not", [Cmp()], pos); return Cmp(); }
    Expr Cmp()
    {
        var l = Sum();
        if (Cur.Kind == T.Sym && Cur.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Next().Text; var r = Sum();
            return new CallExpr(op switch { "==" => "Equal", "!=" => "NotEqual", "<" => "LessThan", "<=" => "LessOrEqual", ">" => "GreaterThan", _ => "GreaterOrEqual" }, [l, r], l.Pos);
        }
        return l;
    }
    Expr Sum()
    {
        var l = Prod();
        while (Cur.Kind == T.Sym && Cur.Text is "+" or "-" or "++")
        {
            var op = Next().Text; var r = Prod();
            l = new CallExpr(op switch { "+" => "Add", "-" => "Subtract", _ => "Concat" }, [l, r], l.Pos);
        }
        return l;
    }
    Expr Prod()
    {
        var l = Unary();
        while (Cur.Kind == T.Sym && Cur.Text is "*" or "/")
        {
            var op = Next().Text; var r = Unary();
            l = new CallExpr(op == "*" ? "Multiply" : "Divide", [l, r], l.Pos);
        }
        return l;
    }
    Expr Unary()
    {
        var pos = Cur.Pos;
        if (TrySym("-")) { var e = Primary(); return e is LitExpr { Value: IntegerValue i } ? new LitExpr(Value.Integer(-i.N), pos) : e is LitExpr { Value: DecimalValue d } ? new LitExpr(Value.Decimal(-d.Q), pos) : new CallExpr("Negate", [e], pos); }
        return Primary();
    }
    Expr Primary()
    {
        var pos = Cur.Pos;
        switch (Cur.Kind)
        {
            case T.Str: return new LitExpr(Value.Text(Next().Text), pos);
            case T.Int: return new LitExpr(Value.Integer(System.Numerics.BigInteger.Parse(Next().Text, CultureInfo.InvariantCulture)), pos);
            case T.Dec: return new LitExpr(Value.Decimal(decimal.Parse(Next().Text, CultureInfo.InvariantCulture)), pos);
            case T.Duration: return new LitExpr(Value.Integer(Lexer.DurationNs(Next().Text)), pos);
            case T.Instant:
                { var t = Next().Text; return Norm.TryParseInstant(t, out var ns) ? new LitExpr(Value.Instant(ns), pos) : throw new SyntaxError("invalid instant literal", pos); }
            case T.Sym when Cur.Text == "(": { p++; var e = Expression(); Sym(")"); return e; }
            case T.Ident:
                {
                    var w = Cur.Text;
                    if (w is "true" or "false") { p++; return new LitExpr(Value.Boolean(w == "true"), pos); }
                    if (w == "null") { p++; return new LitExpr(Value.Null, pos); }
                    // identity expression: ref ( ident : expr ...) ; call: ident ( expr ...) ; path: ident {. ident}
                    var save = p;
                    var r = RefP();
                    if (IsSym("(") && Peek().Kind == T.Ident && Peek(2).Kind == T.Sym && Peek(2).Text == ":")
                    {
                        p++;
                        var comps = new List<(string, Expr, Pos)>();
                        do { var cp = Cur.Pos; var n = Ident(); Sym(":"); comps.Add((n, Expression(), cp)); } while (TrySym(","));
                        Sym(")");
                        return new IdExpr(r, comps, pos);
                    }
                    p = save;
                    var name = Ident();
                    if (IsSym("("))
                    {
                        p++;
                        var args = new List<Expr>();
                        if (!IsSym(")")) { do args.Add(Expression()); while (TrySym(",")); }
                        Sym(")");
                        return new CallExpr(char.ToUpperInvariant(name[0]) + name[1..], args, pos);
                    }
                    var path = name;
                    while (IsSym(".") && Peek().Kind == T.Ident) { p++; path += "." + Ident(); }
                    return new PathExpr(path, pos);
                }
            default: throw Err("expected an expression");
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Sattle.Language;

/// <summary>A refactoring that cannot be applied, with a typed reason (PROTOCOL.md v3).</summary>
public sealed class RefactorError(string code, string message, string? element = null, string[]? include = null) : Exception(message)
{
    /// <summary>Statements that would have to move (or stay) too for the refactoring to be valid.</summary>
    public string[] Include { get; } = include ?? [];
    public string Code { get; } = code;
    public string? Element { get; } = element;
}

/// <summary>Refactorings as pure functions on canonical module text: extract statements into a new stacked projection, inline it back, move a rule. The result is the new text of every touched module; the caller re-lowers it and diffs the definition graph.</summary>
public static partial class Refactor
{
    sealed class Item(string kind, string name, List<string> lines, string[] refs, string[]? emitTypes = null)
    {
        public string Kind = kind, Name = name; public List<string> Lines = lines; public string[] Refs = refs; public string[] Idents = []; public string[]? UpdateKeys = emitTypes;
        public string Text => string.Join("\n", Lines);
    }
    sealed class Block
    {
        public required string Name, Purpose, Output; public bool Forward; public List<string> Ports = []; public string? Retain; public List<Item> Items = [];
        public int Start, End; // line range in the module text, End exclusive
        public bool HasRules => Items.Any(i => i.Kind == "rule");
    }

    [GeneratedRegex(@"^projection (\S+) (\S+)( forward)? output (\S+) \{$")] private static partial Regex HeaderRx();
    [GeneratedRegex(@"^  in (\S+): (rows|graph|param) (\S+);$")] private static partial Regex PortRx();
    [GeneratedRegex(@"^  (let|lift|emit|link|rule|retain|in) ")] private static partial Regex ItemRx();

    /// <summary>Names a statement reads: the source after from, via (the mapping of a lift), series, match, join, union, and a let bound directly to another name.</summary>
    static string[] SourceRefs(string text)
    {
        var toks = Lexer.Lex(text).Where(t => t.Kind != T.Doc).ToList(); var refs = new List<string>();
        for (var i = 0; i < toks.Count - 1; i++)
        {
            if (toks[i].Kind == T.Ident && toks[i].Text is "from" or "via" or "series" or "match" or "join" or "union" or "fixpoint" or "traverse" && toks[i + 1].Kind == T.Ident)
            {
                refs.Add(toks[i + 1].Text);
                if (toks[i].Text is "join" or "union" && i + 3 < toks.Count && toks[i + 2].Text == "," && toks[i + 3].Kind == T.Ident) refs.Add(toks[i + 3].Text);
            }
            else if (i >= 3 && toks[i].Kind == T.Ident && toks[i - 1].Text == "=" && toks[i - 3].Text == "let") refs.Add(toks[i].Text);
        }
        return [.. refs.Distinct()];
    }

    static List<Block> Parse(List<string> lines)
    {
        var res = new List<Block>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = HeaderRx().Match(lines[i]);
            if (!m.Success) continue;
            var b = new Block { Name = m.Groups[1].Value, Purpose = m.Groups[2].Value, Forward = m.Groups[3].Success, Output = m.Groups[4].Value, Start = i };
            var j = i + 1; Item? cur = null; int emits = 0;
            for (; j < lines.Count && lines[j] != "}"; j++)
            {
                var l = lines[j];
                if (ItemRx().Match(l) is { Success: true } im)
                {
                    cur = null;
                    var kind = im.Groups[1].Value;
                    if (kind == "lift") kind = "let";                  // a lift statement names a relation like a let and reads only graph ports
                    if (kind == "in") { b.Ports.Add(l); continue; }
                    if (kind == "retain") { b.Retain = l; continue; }
                    var name = kind switch { "let" => l.Split(' ')[3], "rule" => l.Split(' ')[3], _ => $"emit.{++emits}" };
                    cur = new Item(kind, name, [l], []);
                    b.Items.Add(cur);
                }
                else cur?.Lines.Add(l);
            }
            foreach (var it in b.Items) { it.Refs = SourceRefs(it.Text); it.Idents = [.. Lexer.Lex(it.Text).Where(t => t.Kind == T.Ident).Select(t => t.Text).Distinct()]; }
            b.End = j + 1;
            res.Add(b); i = j;
        }
        return res;
    }

    static string PortName(string line) => PortRx().Match(line).Groups[1].Value;

    static IEnumerable<string> Render(Block b)
    {
        yield return $"projection {b.Name} {b.Purpose}{(b.Forward ? " forward" : "")} output {b.Output} {{";
        foreach (var p in b.Ports) yield return p;
        if (b.Retain is not null) yield return b.Retain;
        foreach (var i in b.Items) foreach (var l in i.Lines) yield return l;
        yield return "}";
    }

    static void Fix(Block b) { if (b.Purpose != "ingestion") b.Forward = !b.HasRules; }

    /// <summary>Statements an update emit depends on: the create emit of the same entity or relationship type.</summary>
    static string? EmitHead(Item i)
    {
        var m = Regex.Match(i.Lines[0], @"^  emit (update )?(entity|relationship) (\S+) ");
        if (!m.Success) return null;
        var key = m.Groups[2].Value == "relationship" ? Regex.Match(i.Text, @" key (\S+):").Groups[1].Value : "";
        return $"{(m.Groups[1].Success ? "u" : "c")}|{m.Groups[2].Value}|{m.Groups[3].Value}|{key}";
    }
    static IEnumerable<string> EmitHeads(Item i)
    {
        var items = i.Kind == "rule" ? i.Lines.Where(l => l.StartsWith("    emit ")).Select(l => new Item("emit", "", ["  " + l.Trim()], [])) : [i];
        foreach (var it in items) if (EmitHead(it) is { } h) yield return h;
    }

    static void Validate(Block from, List<Item> moved, List<Item> kept, string ctxName)
    {
        var movedLets = moved.Where(i => i.Kind == "let").Select(i => i.Name).ToHashSet();
        var allLets = from.Items.Where(i => i.Kind == "let").Select(i => i.Name).ToHashSet();
        foreach (var i in moved)
            foreach (var r in i.Refs.Where(r => allLets.Contains(r) && !movedLets.Contains(r))) throw new RefactorError("DependsOnUnmoved", $"{i.Name} reads {r}, which stays in {from.Name}", i.Name);
        foreach (var i in kept)
            foreach (var r in i.Refs.Where(movedLets.Contains)) throw new RefactorError("SharedOperator", $"{i.Name} reads {r}, which moves", r);
        var movedCreates = moved.SelectMany(EmitHeads).Where(h => h.StartsWith("c|")).Select(h => h[2..]).ToHashSet();
        var keptCreates = kept.SelectMany(EmitHeads).Where(h => h.StartsWith("c|")).Select(h => h[2..]).ToHashSet();
        foreach (var i in moved) foreach (var h in EmitHeads(i).Where(h => h.StartsWith("u|"))) if (!movedCreates.Contains(h[2..]))
            throw new RefactorError("UpdateWithoutCreate", $"{i.Name} updates {h.Split('|')[2]}, whose create emit stays in {from.Name}; move the creating statement too", i.Name, [.. kept.Where(k => EmitHeads(k).Contains("c|" + h[2..])).Select(k => k.Name)]);
        foreach (var i in kept) foreach (var h in EmitHeads(i).Where(h => h.StartsWith("u|"))) if (!keptCreates.Contains(h[2..]))
            throw new RefactorError("UpdateWithoutCreate", $"{i.Name} updates {h.Split('|')[2]}, whose create emit moves; move {i.Name} too", i.Name, [.. kept.Where(k => EmitHeads(k).Contains(h)).Select(k => k.Name).Distinct()]);
        _ = ctxName;
    }

    /// <summary>Adds the lets that only the selection uses (transitively) to the selection.</summary>
    static HashSet<string> Closure(Block b, IEnumerable<string> select)
    {
        var sel = new HashSet<string>(select);
        foreach (var n in sel)
        {
            if (n.Contains('.') && b.Items.Any(i => i.Kind == "rule" && n.StartsWith(i.Name + ".", StringComparison.Ordinal))) throw new RefactorError("SplitRule", $"{n} is a statement inside a rule; select the whole rule", n);
            if (!b.Items.Any(i => i.Name == n)) throw new RefactorError("UnknownSelection", $"{b.Name} has no statement or rule named {n}", n);
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var l in b.Items.Where(i => i.Kind == "let" && !sel.Contains(i.Name)))
            {
                var users = b.Items.Where(i => i.Refs.Contains(l.Name)).ToList();
                if (users.Count > 0 && users.All(u => sel.Contains(u.Name))) { sel.Add(l.Name); changed = true; }
            }
        } while (changed);
        return sel;
    }

    static (List<Item> Moved, List<Item> Kept) Split(Block b, HashSet<string> sel) =>
        ([.. b.Items.Where(i => sel.Contains(i.Name))], [.. b.Items.Where(i => !sel.Contains(i.Name))]);

    static string[] Alias(string text, string module) =>
        [.. Regex.Matches(text, @"^import (\S+) @ \S+ \S+ as (\S+);$", RegexOptions.Multiline).Where(m => m.Groups[1].Value == module).Select(m => m.Groups[2].Value)];

    static List<string> Lines(string text) => [.. text.Replace("\r", "").TrimEnd('\n').Split('\n')];

    static Block Find(List<Block> blocks, string name, string what) => blocks.FirstOrDefault(b => b.Name == name) ?? throw new RefactorError("NotAProjection", $"{what} {name} is not a projection of the module");

    static void Splice(List<string> lines, List<Block> blocks, Block changed, Block? added)
    {
        var repl = Render(changed).ToList();
        if (added is not null) { repl.Add(""); repl.AddRange(Render(added)); }
        lines.RemoveRange(changed.Start, changed.End - changed.Start);
        lines.InsertRange(changed.Start, repl);
        _ = blocks;
    }

    static Regex Binding(string alias, string projection) => new($@"^  (?:master )?graph (\S+) = {Regex.Escape(alias)}\.{Regex.Escape(projection)}\(([^)]*)\);\n", RegexOptions.Multiline);

    /// <summary>Rewrites every module that imports `module` and binds `projection` in a graph space.</summary>
    static Dictionary<string, string> UpdateSpaces(IReadOnlyDictionary<string, string> texts, string module, string projection, Func<string, Match, string> edit)
    {
        var res = new Dictionary<string, string>();
        foreach (var (m, text) in texts)
        {
            if (m == module) continue;
            var t = text.Replace("\r", "");
            foreach (var a in Alias(t, module)) t = Binding(a, projection).Replace(t, mm => edit(a, mm));
            if (t != text) res[m] = t;
        }
        return res;
    }

    /// <summary>Moves the selected statements into a new projection with its own output graph, bound in the graph space like the parent.</summary>
    public static Dictionary<string, string> Extract(IReadOnlyDictionary<string, string> texts, string module, string projection, IReadOnlyList<string> select, string name)
    {
        if (!texts.TryGetValue(module, out var text)) throw new RefactorError("CrossModule", $"module {module} is not in the draft");
        var lines = Lines(text); var blocks = Parse(lines);
        var from = Find(blocks, projection, "");
        if (from.Purpose == "ingestion") throw new RefactorError("IngestionProjection", $"{projection} is the master's ingestion; a second graph must derive from the master, so its rules cannot be split off");
        if (blocks.Any(b => b.Name == name)) throw new RefactorError("BadRequest", $"projection {name} exists");
        var sel = Closure(from, select);
        if (sel.Count == 0) throw new RefactorError("UnknownSelection", "nothing is selected");
        var (moved, kept) = Split(from, sel);
        Validate(from, moved, kept, name);
        var usedPorts = moved.SelectMany(i => i.Refs).Concat(moved.SelectMany(i => i.Idents).Where(n => from.Ports.Any(p => p.Contains(": param ") && PortName(p) == n))).ToHashSet();
        var q = new Block { Name = name, Purpose = from.Purpose, Output = from.Output, Ports = [.. from.Ports.Where(p => usedPorts.Contains(PortName(p)))], Retain = from.Retain, Items = moved };
        from.Items = kept; Fix(from); Fix(q);
        Splice(lines, blocks, from, q);
        var res = new Dictionary<string, string> { [module] = string.Join("\n", lines) + "\n" };
        var ports = q.Ports.Select(PortName).ToHashSet();
        foreach (var (m, t) in UpdateSpaces(texts, module, projection, (alias, mm) =>
        {
            var args = string.Join(", ", Regex.Split(mm.Groups[2].Value.Trim(), @",\s*").Where(a => ports.Contains(a.Split(':')[0].Trim())).Select(a => Regex.Replace(a.Trim(), @"\s+", " ")));
            return mm.Value + $"  graph {mm.Groups[1].Value}_{name} = {alias}.{name}({args});\n";
        })) res[m] = t;
        return res;
    }

    /// <summary>The reverse of Extract: the projection's statements go back into another one and its graph leaves the space.</summary>
    public static Dictionary<string, string> Inline(IReadOnlyDictionary<string, string> texts, string module, string projection, string into)
    {
        if (!texts.TryGetValue(module, out var text)) throw new RefactorError("CrossModule", $"module {module} is not in the draft");
        var lines = Lines(text); var blocks = Parse(lines);
        var q = Find(blocks, projection, ""); var p = Find(blocks, into, "");
        if (q.Name == p.Name) throw new RefactorError("BadRequest", "a projection cannot be inlined into itself");
        if (q.Output != p.Output) throw new RefactorError("CrossModule", "the projections have different output ontologies");
        var names = p.Items.Select(i => i.Name).ToHashSet();
        foreach (var i in q.Items.Where(i => i.Kind is "let" or "rule" && names.Contains(i.Name))) throw new RefactorError("BadRequest", $"{into} already has a statement named {i.Name}", i.Name);
        foreach (var port in q.Ports) if (!p.Ports.Any(x => PortName(x) == PortName(port))) p.Ports.Add(port);
        p.Items.AddRange(q.Items); Fix(p);
        foreach (var b in new[] { p, q }.OrderByDescending(b => b.Start))
        {
            var drop = b == q; var start = b.Start - (drop && b.Start > 0 && lines[b.Start - 1] == "" ? 1 : 0);
            lines.RemoveRange(start, b.End - start);
            if (!drop) lines.InsertRange(start, Render(p));
        }
        var res = new Dictionary<string, string> { [module] = string.Join("\n", lines) + "\n" };
        foreach (var (m, t) in UpdateSpaces(texts, module, projection, (_, _) => "")) res[m] = t;
        return res;
    }

    /// <summary>Moves a rule, and the lets only it uses, from one projection to another of the same module.</summary>
    public static Dictionary<string, string> MoveRule(IReadOnlyDictionary<string, string> texts, string module, string fromName, string toName, string rule)
    {
        if (!texts.TryGetValue(module, out var text)) throw new RefactorError("CrossModule", $"module {module} is not in the draft");
        var lines = Lines(text); var blocks = Parse(lines);
        var from = Find(blocks, fromName, "from"); var to = Find(blocks, toName, "to");
        if (from.Name == to.Name) throw new RefactorError("BadRequest", "source and target are the same projection");
        if (from.Output != to.Output) throw new RefactorError("CrossModule", "the projections have different output ontologies");
        if (!from.Items.Any(i => i.Kind == "rule" && i.Name == rule)) throw new RefactorError("UnknownSelection", $"{fromName} has no rule {rule}", rule);
        var sel = Closure(from, [rule]);
        var (moved, kept) = Split(from, sel);
        Validate(from, moved, kept, toName);
        foreach (var i in moved.Where(i => to.Items.Any(x => x.Name == i.Name && x.Kind == i.Kind && i.Kind != "emit"))) throw new RefactorError("BadRequest", $"{toName} already has {i.Name}", i.Name);
        var usedPorts = moved.SelectMany(i => i.Refs).Concat(moved.SelectMany(i => i.Idents).Where(n => from.Ports.Any(p => p.Contains(": param ") && PortName(p) == n))).ToHashSet();
        var addedPorts = new List<string>();
        foreach (var port in from.Ports.Where(p => usedPorts.Contains(PortName(p)))) if (!to.Ports.Any(x => PortName(x) == PortName(port))) { to.Ports.Add(port); addedPorts.Add(PortName(port)); }
        to.Items.AddRange(moved); from.Items = kept; Fix(from); Fix(to);
        // rewrite both blocks, later one first so line numbers stay valid
        foreach (var b in new[] { from, to }.OrderByDescending(b => b.Start)) { var r = Render(b).ToList(); lines.RemoveRange(b.Start, b.End - b.Start); lines.InsertRange(b.Start, r); }
        var res = new Dictionary<string, string> { [module] = string.Join("\n", lines) + "\n" };
        // a port the target did not have is bound in the space like the source's port of that name
        if (addedPorts.Count > 0)
            foreach (var (m, text0) in texts)
            {
                if (m == module) continue;
                var t = text0.Replace("\r", "");
                foreach (var a in Alias(t, module))
                {
                    if (Binding(a, fromName).Match(t) is not { Success: true } fm) continue;
                    var fromArgs = Regex.Split(fm.Groups[2].Value.Trim(), @",\s*").Select(x => Regex.Replace(x.Trim(), @"\s+", " ")).Where(x => x.Length > 0).ToList();
                    t = Binding(a, toName).Replace(t, mm =>
                    {
                        var have = Regex.Split(mm.Groups[2].Value.Trim(), @",\s*").Select(x => Regex.Replace(x.Trim(), @"\s+", " ")).Where(x => x.Length > 0).ToList();
                        foreach (var port in addedPorts) if (!have.Any(x => x.Split(':')[0].Trim() == port) && fromArgs.FirstOrDefault(x => x.Split(':')[0].Trim() == port) is { } arg) have.Add(arg);
                        return mm.Value.Replace("(" + mm.Groups[2].Value + ")", "(" + string.Join(", ", have) + ")");
                    });
                }
                if (t != text0) res[m] = t;
            }
        return res;
    }
}

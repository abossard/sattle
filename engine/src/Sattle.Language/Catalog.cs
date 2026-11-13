using System.Collections.Immutable;
using System.Text;
using Sattle.Core;

namespace Sattle.Language;

public sealed record SourceFile(string Path, string Text);
public sealed record ModuleInfo(string Name, string Version, string Digest, string ReleaseId, string File, ModuleAst Ast);
public sealed record PinFix(string File, Pos DigestPos, string Module, string Expected, string? Found);

/// <summary>Projections written in another language than a module's text, in files of their own beside it.</summary>
public interface IProjectionSource
{
    /// <summary>The projections of `module` in `files` (every definition file that is not a module text), read against `catalog`,
    /// which holds the module's own text and everything it imports.</summary>
    ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files);
}

/// <summary>The facts of a module's projections, by projection id; the file and position each element comes from; and what is wrong.</summary>
public sealed record ProjectionFacts(IReadOnlyDictionary<string, IReadOnlyList<Element>> Projections, IReadOnlyDictionary<string, (string File, Pos Pos)> Places, IReadOnlyList<Diagnostic> Diagnostics);

public sealed class LoadResult(List<Diagnostic> diagnostics, List<PinFix> pinFixes)
{
    public List<Diagnostic> Diagnostics { get; } = diagnostics;
    public List<PinFix> PinFixes { get; } = pinFixes;
    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}

/// <summary>The catalog graph (research 03 section 2.1): the kernel plus every loaded module, definitions as ordinary graph data with one commit per module.</summary>
public sealed class Catalog
{
    public GraphLog Log { get; } = new("catalog");
    public Graph Graph => Log.State;
    public Dictionary<string, ModuleInfo> Modules { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Pos> SourceMap { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> FileOf { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TypedProjection> Projections { get; } = new(StringComparer.Ordinal);
    /// <summary>Per projection id, the operators that typed, with their output row schemas: also for a projection with errors, whose operators before the error typed.</summary>
    public Dictionary<string, IReadOnlyList<TOp>> OperatorTypes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TypedSpace> Spaces { get; } = new(StringComparer.Ordinal);

    readonly IProjectionSource? projections;

    /// <param name="projections">projections written in files of their own: each takes the place of the module's projection of the same id, or is added to the module</param>
    public Catalog(IProjectionSource? projections = null)
    {
        this.projections = projections;
        var kernelRelease = $"d:{Ids.KernelModule}@{Kernel.Version}";
        var edits = Kernel.Graph.Elements().Select(Edit.Create).ToList();
        edits.Add(Edit.Create(new Entity(kernelRelease, Ids.KEntity("Release"), Props.Of(
            (Ids.KProp("version"), Value.Text(Kernel.Version)), (Ids.KProp("digest"), Value.Text(Kernel.CompiledDigest)),
            (Ids.KProp("revision"), Value.Text("1")), (Ids.KProp("status"), Value.Text("active"))))));
        var rid = Ids.Rel(kernelRelease, "releaseOf", Kernel.ModuleId);
        edits.Add(Edit.Create(new Rel(rid, kernelRelease, Kernel.ModuleId, Ids.KRel("releaseOf"), Props.Empty)));
        var (_, err) = Log.Commit(edits);
        if (err is not null) throw new InvalidOperationException(err);
        Modules[Ids.KernelModule] = new ModuleInfo(Ids.KernelModule, Kernel.Version, Kernel.CompiledDigest, kernelRelease, "<kernel>", new ModuleAst(Ids.KernelModule, Kernel.Version, null, [], [], default));
    }

    public static List<SourceFile> ReadDirectory(string dir)
    {
        List<SourceFile> files = [.. Directory.EnumerateFiles(dir, "*.sattle").Order(StringComparer.Ordinal).Select(f => new SourceFile(f, File.ReadAllText(f)))];
        return AfterRead?.Invoke(files, dir) ?? files;
    }

    /// <summary>A step applied to what `ReadDirectory` read, given the files and the folder. A seam for test runs, until the catalog reads projections written in KQL itself: the test assemblies set it when `SATTLE_PROJECTIONS=kql`, so every test reads its projections from the `.kql` files. Nothing in the engine or the CLI sets it; null changes nothing.</summary>
    public static Func<List<SourceFile>, string, List<SourceFile>>? AfterRead { get; set; }

    /// <summary>Digest of a module release (section 2.9): sha256 of the canonical JSON of the sorted facts of the contains-closure, the edges leaving it and the import triples.</summary>
    public static string ModuleDigest(Graph g, string module, IEnumerable<(string Module, string Version, string Digest)> imports)
    {
        var d = new DefView(g);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(); stack.Push(Ids.Module(module));
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!closure.Add(n)) continue;
            foreach (var r in d.Out(n, "contains")) stack.Push(r.Target);
        }
        var facts = new List<(string Key, Element E)>();
        foreach (var n in closure) { facts.Add(("e:" + n, g.Entity(n)!)); foreach (var rid in g.OutRels(n)) facts.Add(("r:" + rid, g.Rel(rid)!)); }
        facts.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var sb = new StringBuilder("[[");
        for (var i = 0; i < facts.Count; i++) { if (i > 0) sb.Append(','); sb.Append('['); Jcs.Str(sb, facts[i].Key); sb.Append(','); Jcs.Element(sb, facts[i].E); sb.Append(']'); }
        sb.Append("],[");
        var triples = imports.OrderBy(x => x.Module, StringComparer.Ordinal).ToList();
        for (var i = 0; i < triples.Count; i++) { if (i > 0) sb.Append(','); sb.Append('['); Jcs.Str(sb, triples[i].Module); sb.Append(','); Jcs.Str(sb, triples[i].Version); sb.Append(','); Jcs.Str(sb, triples[i].Digest); sb.Append(']'); }
        return Digests.Of(sb.Append("]]").ToString());
    }

    Diagnostic Locate(Diagnostic x)
    {
        if (x.File is not null) return x;
        var id = x.Focus;
        while (id.Length > 0)
        {
            if (SourceMap.TryGetValue(id, out var pos) && FileOf.TryGetValue(id, out var f)) return x with { File = f, Line = pos.Line, Col = pos.Col };
            var cut = id.LastIndexOf('/');
            if (cut < 0) break;
            id = id[..cut];
        }
        return x;
    }

    public LoadResult Load(IEnumerable<SourceFile> files, bool updatePins = false)
    {
        var diags = new List<Diagnostic>(); var fixes = new List<PinFix>();
        var parsed = new List<(SourceFile File, ModuleAst Ast)>();
        // a definition file that is not a module text is read by the projection source, if there is one
        var others = files.Where(f => !f.Path.EndsWith(".sattle", StringComparison.Ordinal)).ToList();
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => f.Path.EndsWith(".sattle", StringComparison.Ordinal)))
        {
            try { parsed.Add((f, Parser.Parse(f.Text))); }
            catch (SyntaxError e) { diags.Add(new Diagnostic("Syntax", "error", "", e.Message, f.Path, e.Pos.Line, e.Pos.Col)); }
        }
        // K5: the import graph is acyclic; load in dependency order
        var byName = new Dictionary<string, (SourceFile File, ModuleAst Ast)>(StringComparer.Ordinal);
        foreach (var p in parsed)
            if (!byName.TryAdd(p.Ast.Name, p) || Modules.ContainsKey(p.Ast.Name))
                diags.Add(new Diagnostic("K5", "error", Ids.Module(p.Ast.Name), $"module {p.Ast.Name} is declared twice", p.File.Path, p.Ast.Pos.Line, p.Ast.Pos.Col));
        var order = new List<string>(); var mark = new Dictionary<string, int>();
        void Visit(string m, Stack<string> path)
        {
            if (mark.GetValueOrDefault(m) == 2) return;
            if (mark.GetValueOrDefault(m) == 1)
            {
                var ast = byName[m].Ast;
                diags.Add(new Diagnostic("K5", "error", Ids.Module(m), $"import cycle: {string.Join(" -> ", path.Reverse().Append(m))}", byName[m].File.Path, ast.Pos.Line, ast.Pos.Col));
                return;
            }
            mark[m] = 1; path.Push(m);
            foreach (var im in byName[m].Ast.Imports.Where(i => byName.ContainsKey(i.Module))) Visit(im.Module, path);
            path.Pop(); mark[m] = 2; order.Add(m);
        }
        foreach (var m in byName.Keys.Order(StringComparer.Ordinal)) Visit(m, new Stack<string>());

        foreach (var m in order)
        {
            var (file, ast) = byName[m];
            if (Modules.ContainsKey(m)) continue;
            var before = diags.Count;
            LoadModule(file, ast, diags, fixes, updatePins, others, read);
            if (diags.Skip(before).Any(x => x.IsError)) { /* module not committed; dependents report UnknownImport */ }
        }
        diags.AddRange(Unread(others, read));
        return new LoadResult([.. diags.Select(Locate)], fixes);
    }

    /// <summary>A definition file that is not a module text and that no module read: an error with its name, never ignored.</summary>
    static IEnumerable<Diagnostic> Unread(IEnumerable<SourceFile> others, IReadOnlySet<string> read) =>
        others.Where(f => !read.Contains(f.Path)).Select(f => new Diagnostic("UnreadFile", "error", "", $"no module read {Path.GetFileName(f.Path)}: its module is missing or did not load, or nothing reads files of its kind", f.Path, 1, 1));

    void LoadModule(SourceFile file, ModuleAst ast, List<Diagnostic> diags, List<PinFix> fixes, bool updatePins, IReadOnlyList<SourceFile> others, HashSet<string> read)
    {
        var mid = Ids.Module(ast.Name);
        var local = new List<Diagnostic>();
        var low = Lowering.Lower(ast, Graph, file.Path);
        local.AddRange(low.Diagnostics);
        foreach (var kv in low.SourceMap) { SourceMap[kv.Key] = kv.Value; FileOf[kv.Key] = file.Path; }
        if (low.Diagnostics.Any(x => x.IsError)) { diags.AddRange(local); return; }

        // imports: version and pin
        var imports = new List<(string, string, string)>();
        foreach (var im in ast.Imports)
        {
            if (!Modules.TryGetValue(im.Module, out var target)) continue; // reported by lowering
            if (target.Version != im.Version) local.Add(new Diagnostic("K5", "error", mid, $"import {im.Module} @ {im.Version}: loaded release is {target.Version}", file.Path, im.Pos.Line, im.Pos.Col));
            if (im.Digest != target.Digest)
            {
                fixes.Add(new PinFix(file.Path, im.DigestPos, im.Module, target.Digest, im.Digest));
                if (!updatePins) local.Add(new Diagnostic(im.Digest is null ? "ImportPinMissing" : "ImportPinMismatch", "error", mid,
                    $"import {im.Module} @ {im.Version} needs pin {target.Digest}" + (im.Digest is null ? "" : $", found {im.Digest}"), file.Path, im.DigestPos.Line, im.DigestPos.Col));
            }
            imports.Add((im.Module, im.Version, target.Digest));
        }

        var work = Graph.Clone();
        var elements = low.Builder.Elements().ToList();
        var err = work.Apply([.. elements.Select(Edit.Create)]);
        if (err is not null) { local.Add(new Diagnostic("Internal", "error", mid, err)); diags.AddRange(local); return; }
        // projections in files of their own, read against the module's text: each replaces what the module's projection of the same id owns
        if (projections is not null && others.Count > 0)
        {
            var other = projections.Lower(work, ast.Name, others);
            read.UnionWith(other.Places.Values.Select(p => p.File).Concat(other.Diagnostics.Select(x => x.File).OfType<string>()));
            local.AddRange(other.Diagnostics);
            if (other.Diagnostics.Any(x => x.IsError)) { diags.AddRange(local); return; }
            bool Under(string id) => other.Projections.Keys.Any(p => id == p || id.StartsWith(p + "/", StringComparison.Ordinal));
            // owned: the elements under it, the rels from them, and the rel that contains it; a rel from outside (a space's producedBy, bindsPort) stays
            elements = [.. elements.Where(e => e is Entity en ? !Under(en.Id) : e is Rel r && !Under(r.Source) && !(Ids.Name(r.Type) == "contains" && other.Projections.ContainsKey(r.Target))),
                .. other.Projections.Values.SelectMany(f => f)];
            foreach (var id in SourceMap.Keys.Where(Under).ToList()) { SourceMap.Remove(id); FileOf.Remove(id); }
            foreach (var (id, (f, pos)) in other.Places) { SourceMap[id] = pos; FileOf[id] = f; }
            work = Graph.Clone();
            err = work.Apply([.. elements.Select(Edit.Create)]);
            if (err is not null) { local.Add(new Diagnostic("Internal", "error", mid, err)); diags.AddRange(local); return; }
        }
        var facts = elements.Select(Edit.Create).ToList();

        // structural conformance conforms(D, K), kernel constraints, reference closure (K4)
        var kview = KernelView();
        var inModule = new HashSet<string>(elements.OfType<Entity>().Select(e => e.Id), StringComparer.Ordinal);
        local.AddRange(Conformance.Check(work, kview).Where(x => inModule.Contains(x.Focus) || Ids.ModuleOf(x.Focus) == ast.Name));
        local.AddRange(KernelChecks.Run(work).Where(x => Ids.ModuleOf(x.Focus) == ast.Name));
        var closure = ProjectionChecker.Closure(work, ast.Name);
        foreach (var r in elements.OfType<Rel>().Where(r => Ids.Name(r.Type) != "contains"))
        {
            var tm = Ids.ModuleOf(r.Target);
            if (!closure.Contains(tm)) local.Add(new Diagnostic("K4", "error", r.Source, $"reference to {r.Target} leaves the import closure of {ast.Name}"));
        }
        local.AddRange(OntologyChecks(work, ast.Name));

        // projections and spaces
        if (!local.Any(x => x.IsError))
        {
            var d = new DefView(work);
            var checker = new ProjectionChecker(work);
            var typed = new Dictionary<string, TypedProjection>();
            foreach (var p in d.Children(mid, "projection"))
            {
                var (tp, pd) = checker.Check(p);
                OperatorTypes[p] = [.. checker.Typed.Values];
                local.AddRange(pd);
                if (tp is not null) typed[p] = tp;
            }
            foreach (var s in d.Children(mid, "space"))
            {
                var all = new Dictionary<string, TypedProjection>(Projections);
                foreach (var kv in typed) all[kv.Key] = kv.Value;
                var (ts, sd) = new SpaceChecker(work).Check(s, all);
                local.AddRange(sd);
                if (ts is not null && !local.Any(x => x.IsError)) Spaces[s] = ts;
            }
            if (!local.Any(x => x.IsError)) foreach (var kv in typed) Projections[kv.Key] = kv.Value;
        }
        diags.AddRange(local);
        if (local.Any(x => x.IsError)) { foreach (var s in Spaces.Keys.Where(k => k.StartsWith(mid + "/", StringComparison.Ordinal)).ToList()) Spaces.Remove(s); return; }

        // seal: release node, digest, commit
        var digest = ModuleDigest(work, ast.Name, imports);
        var rel = $"d:{ast.Name}@{ast.Version}";
        var rev = (Log.Head + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var release = new Entity(rel, Ids.KEntity("Release"), Props.Of(
            (Ids.KProp("version"), Value.Text(ast.Version)), (Ids.KProp("digest"), Value.Text(digest)), (Ids.KProp("revision"), Value.Text(rev)), (Ids.KProp("status"), Value.Text("active"))));
        facts.Add(Edit.Create(release));
        facts.Add(Edit.Create(new Rel(Ids.Rel(rel, "releaseOf", mid), rel, mid, Ids.KRel("releaseOf"), Props.Empty)));
        var (_, cerr) = Log.Commit(facts, cause: new Cause([file.Path], "0"));
        if (cerr is not null) { diags.Add(new Diagnostic("Internal", "error", mid, cerr)); return; }
        Modules[ast.Name] = new ModuleInfo(ast.Name, ast.Version, digest, rel, file.Path, ast);
    }

    static OntologyView? kview;
    static OntologyView KernelView() => kview ??= OntologyView.Build(Kernel.Graph, [Ids.KernelModule]);

    /// <summary>Ontology well-formedness (section 3.1, item 3), K10, identity domains.</summary>
    static List<Diagnostic> OntologyChecks(Graph work, string module)
    {
        var res = new List<Diagnostic>();
        void Err(string code, string focus, string msg) => res.Add(new Diagnostic(code, "error", focus, msg));
        var d = new DefView(work);
        var mid = Ids.Module(module);
        var view = OntologyView.Build(work, ProjectionChecker.Closure(work, module));
        foreach (var t in view.Types.Values.Where(t => Ids.ModuleOf(t.Id) == module))
        {
            foreach (var grp in view.PropsOf(t.Id).Keys.Where(view.Props.ContainsKey).GroupBy(p => view.Props[p].Name).Where(g => g.Count() > 1))
                Err("PropertyClash", t.Id, $"{t.Name} has several properties named '{grp.Key}': {string.Join(", ", grp.Select(x => Ids.ModuleOf(x)))}");
            foreach (var (p, req) in t.OwnProps)
                if (req && view.Props.TryGetValue(p, out var pi) && pi.Role == "telemetry") Err("K10", t.Id, $"telemetry property {pi.Name} is never required");
        }
        foreach (var c in view.Constraints.Where(c => Ids.ModuleOf(c.Id) == module))
        {
            var isRel = view.Types.TryGetValue(c.TypeId, out var ti) && ti.IsRel;
            var pk = c.PropId is not null && view.Props.TryGetValue(c.PropId, out var pi) ? pi.ValueKind : null;
            switch (c.Kind)
            {
                case "enum" or "pattern" when pk != "Text": Err("ConstraintFit", c.Id, $"{c.Kind} constraint needs a Text property"); break;
                case "range" when pk is not ("Integer" or "Decimal" or "Instant"): Err("ConstraintFit", c.Id, "range constraint needs a numeric or Instant property"); break;
                case "cardinality" or "acyclic" when !isRel: Err("ConstraintFit", c.Id, $"{c.Kind} constraint needs a relationship type"); break;
                case "key" when isRel: Err("ConstraintFit", c.Id, "key constraint needs an entity type"); break;
            }
            if (c.Kind is "enum" or "pattern" or "range" && c.PropId is null) Err("ConstraintFit", c.Id, $"{c.Kind} constraint needs a property");
        }
        foreach (var dom in d.Children(mid, "identity"))
            foreach (var comp in d.Children(dom, "component"))
            {
                var canon = d.Target(comp, "canonicalizer");
                var vk = Ids.Name(d.Target(comp, "valueKind") ?? "");
                if (canon is not null && Ids.Name(canon) != "canon.Exact" && vk != "Text") Err("Canonicalizer", comp, $"canonicalizer {Ids.Name(canon)[6..]} accepts Text, component is {vk}");
            }
        return res;
    }

    /// <summary>The ontology view of a module closure, for data graph conformance of a projection output.</summary>
    public OntologyView OntologyOf(string module) => OntologyView.Build(Graph, ProjectionChecker.Closure(Graph, module));

    /// <summary>Applies pin fixes to source text. Returns the new text per file.</summary>
    public static Dictionary<string, string> ApplyPinFixes(IEnumerable<SourceFile> files, IEnumerable<PinFix> fixes)
    {
        var res = new Dictionary<string, string>();
        foreach (var f in files)
        {
            var mine = fixes.Where(x => x.File == f.Path).OrderByDescending(x => x.DigestPos.Line).ThenByDescending(x => x.DigestPos.Col).ToList();
            if (mine.Count == 0) continue;
            var lines = f.Text.Split('\n').ToList();
            foreach (var fx in mine)
            {
                var line = lines[fx.DigestPos.Line - 1];
                var start = fx.DigestPos.Col - 1;
                var end = start; while (end < line.Length && (char.IsAsciiHexDigit(line[end]) || line[end] == ':' || line[end] == '…' || "sha".Contains(line[end]))) end++;
                lines[fx.DigestPos.Line - 1] = line[..start] + fx.Expected + line[end..];
            }
            res[f.Path] = string.Join('\n', lines);
        }
        return res;
    }
}

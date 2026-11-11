using System.Text;
using Sattle.Core;

namespace Sattle.Language;

/// <summary>What a lift statement was lowered to: its name, the relationship it lifts to and the typed operators generated for it (in plan order).</summary>
public sealed record TLiftInfo(string Name, string Id, string Emit, string EdgeType, string MapType, string Images, IReadOnlyList<string> Ops);

/// <summary>Lowering of the `Lift` operator (README, "Edge lifting"). The definition graph keeps one Lift node per statement; when a projection is checked, each Lift node is replaced, in a copy of the graph, by the operators it stands for (Match, Join, Group, Filter, Series, Rollup, Map, Threshold and the emits of the lifted relationship), which the checker and the planner then treat like operators written by hand: incremental strategy, batch invariance, lineage and retention come with them. The expansion is generated as text from the node, parsed and lowered by the ordinary front end, and spliced in at the id of the Lift node, so the relation the statement names stays wired to its readers.</summary>
public static class LiftExpander
{
    public static bool HasLift(Graph defs, string pid) { var d = new DefView(defs); return d.Children(pid, "op").Any(o => d.TypeName(o) == "Lift"); }

    public sealed record Expansion(Graph Graph, List<Diagnostic> Diagnostics, List<(string Id, string Name)> Lifts);

    static Diagnostic Err(string op, string name, string message) => new("Lift", "error", op, $"lift {name}: {message}");

    public static Expansion Expand(Graph defs, string pid)
    {
        var d = new DefView(defs);
        var diags = new List<Diagnostic>(); var lifts = new List<(string, string)>();
        var g = defs.Clone();
        var module = Ids.ModuleOf(pid);
        foreach (var op in d.Children(pid, "op").Where(o => d.TypeName(o) == "Lift").OrderBy(o => o, StringComparer.Ordinal))
        {
            var name = d.Text(op, "name") ?? Ids.Name(op);
            var errors = new List<Diagnostic>();
            var text = Generate(defs, d, pid, op, name, errors);
            if (text is null) { diags.AddRange(errors); continue; }
            if (Environment.GetEnvironmentVariable("SATTLE_LIFT_DEBUG") is not null) Console.Error.WriteLine(text);
            ModuleAst ast;
            try { ast = Parser.Parse(text); }
            catch (SyntaxError e) { diags.Add(Err(op, name, $"the generated expansion does not parse ({e.Message})")); continue; }
            var low = Lowering.LowerFragment(ast, defs, "<lift " + name + ">", name + ".");
            if (low.Diagnostics.Any(x => x.IsError)) { diags.AddRange(low.Diagnostics.Where(x => x.IsError).Select(x => Err(op, name, x.Message))); continue; }
            var rule = defs.InRels(op).Select(defs.Rel).FirstOrDefault(r => r is not null && Ids.Name(r!.Type) == "governs")?.Source;
            if (!Splice(g, d, pid, op, low.Builder, out var why)) { diags.Add(Err(op, name, why!)); continue; }
            // a lift in a readonly rule: the rule governs the emits it was lowered to
            if (rule is not null)
            {
                var sinks = low.Builder.Nodes.Values.Where(n => Ids.Name(n.Type) is "EmitEntity" or "EmitRelationship").Select(n => n.Id).ToList();
                var gerr = g.Apply([.. sinks.Select(sk => Edit.Create(new Rel(Ids.Rel(rule, "governs", sk), rule, sk, Ids.KRel("governs"), Props.Empty)))]);
                if (gerr is not null) { diags.Add(Err(op, name, "the rule could not govern the expansion: " + gerr)); continue; }
            }
            lifts.Add((op, name));
        }
        return new Expansion(g, diags, lifts);
    }

    /// <summary>Replaces the Lift node by the generated operators: its subtree and the relationships that touch it go (the wires out of its relation stay), the generated facts that the graph does not have yet come in.</summary>
    static bool Splice(Graph g, DefView d, string pid, string op, DefBuilder low, out string? why)
    {
        why = null;
        var sub = new HashSet<string>(StringComparer.Ordinal); var todo = new Stack<string>(); todo.Push(op);
        while (todo.Count > 0) { var n = todo.Pop(); if (!sub.Add(n)) continue; foreach (var c in d.Out(n, "contains")) todo.Push(c.Target); }
        var outPort = Ids.Child(op, "port", "out");
        var incident = g.Rels.Where(r => sub.Contains(r.Source) || sub.Contains(r.Target)).ToList();
        var keep = incident.Where(r => Ids.Name(r.Type) == "wire" && r.Source == outPort).ToList();
        var edits = incident.Select(Edit.Remove).Concat(sub.Select(g.Entity).Where(e => e is not null).Select(e => Edit.Remove(e!))).ToList();
        var err = g.Apply(edits);
        if (err is not null) { why = "the lift could not be taken out of the graph: " + err; return false; }
        var adds = low.Elements().Where(e => e is Entity en ? g.Entity(en.Id) is null : g.Rel(((Rel)e).Id) is null).Concat(keep.Cast<Element>()).ToList();
        err = g.Apply([.. adds.OfType<Entity>().Select(Edit.Create), .. adds.OfType<Rel>().Select(Edit.Create)]);
        if (err is not null) { why = "the expansion does not fit the graph: " + err; return false; }
        _ = pid;
        return true;
    }

    // ---- the text of the expansion ----------------------------------------------------------------------------------------------------

    static string? Generate(Graph defs, DefView d, string pid, string op, string name, List<Diagnostic> errors)
    {
        void Fail(string m) => errors.Add(Err(op, name, m));
        var module = Ids.ModuleOf(pid);
        var inRule = defs.InRels(op).Select(defs.Rel).Any(r => r is not null && Ids.Name(r!.Type) == "governs");
        if (!d.Bool(pid, "forward") && !inRule) { Fail("a lift belongs to a forward projection or to a readonly rule"); return null; }
        var outTarget = d.Target(pid, "outputOntology"); var emitT = d.Target(op, "ofType"); var edgeT = d.Target(op, "liftEdge"); var mapT = d.Target(op, "liftVia");
        if (outTarget is null || emitT is null || edgeT is null || mapT is null) { Fail("the statement lacks the relationship to lift to, the edge type or the mapping"); return null; }
        string? PortName(string port) { var w = defs.InRels(Ids.Child(op, "port", port)).Select(defs.Rel).FirstOrDefault(r => r is not null && Ids.Name(r!.Type) == "wire"); return w is null ? null : w.Source; }
        var ePort = PortName("edges"); var mPort = PortName("map");
        if (ePort is null || mPort is null) { Fail("the edge graph and the mapping graph must be graph ports of the projection"); return null; }
        string? Expects(string port) => d.Target(port, "expects") is { } x ? Ids.ModuleOf(x) : null;
        var eMod = Expects(ePort); var mMod = Expects(mPort);
        if (eMod is null || mMod is null) { Fail("a graph port of the lift expects no module"); return null; }
        var outMod = Ids.ModuleOf(outTarget);
        var edgeView = OntologyView.Build(defs, ProjectionChecker.Closure(defs, eMod));
        var mapView = OntologyView.Build(defs, ProjectionChecker.Closure(defs, mMod));
        var outView = OntologyView.Build(defs, ProjectionChecker.Closure(defs, outMod));
        string Nm(string id) => Ids.Name(id);
        if (!edgeView.Types.TryGetValue(edgeT, out var edgeInfo) || !edgeInfo.IsRel) { Fail($"{Nm(edgeT)} is not a relationship type of the edge graph {ePortName()}"); return null; }
        if (!mapView.Types.TryGetValue(mapT, out var mapInfo) || !mapInfo.IsRel) { Fail($"{Nm(mapT)} is not a relationship type of the mapping graph {mPortName()}"); return null; }
        if (!outView.Types.TryGetValue(emitT, out var emitInfo) || !emitInfo.IsRel) { Fail($"{Nm(emitT)} is not a relationship type of the output ontology {outMod}"); return null; }
        string ePortName() => d.Text(ePort, "name") ?? Nm(ePort); string mPortName() => d.Text(mPort, "name") ?? Nm(mPort);
        if (edgeInfo.Sources.Count != 1 || edgeInfo.Targets.Count != 1) { Fail($"{Nm(edgeT)} must declare exactly one source type and one target type"); return null; }
        if (mapInfo.Sources.Count != 1 || mapInfo.Targets.Count != 1) { Fail($"{Nm(mapT)} must declare exactly one source type and one target type (the node and its image)"); return null; }
        var srcT = edgeInfo.Sources.First(); var dstT = edgeInfo.Targets.First(); var mapSrcT = mapInfo.Sources.First(); var imgT = mapInfo.Targets.First();

        // the mapping is a function, or the statement says what happens to a node with several images
        var images = d.Text(op, "images");
        var function = mapView.Constraints.Any(c => c.Kind == "cardinality" && c.TypeId == mapT && (c.Side ?? "source") == "source" && c.Max == 1m);
        if (images is null && !function) { Fail($"{Nm(mapT)} is not declared a function (constraint cardinality {Nm(mapT)} per source 0 .. 1) and the statement states no policy for a node with several images (images reject or images each)"); return null; }
        images ??= "reject";

        // properties
        var emitProps = outView.PropsOf(emitT).Keys.Select(p => outView.Props[p]).ToDictionary(p => p.Name);
        var edgeProps = edgeView.PropsOf(edgeT).Keys.Select(p => edgeView.Props[p]).ToDictionary(p => p.Name);
        var carry = new List<(string Target, string Source)>();
        foreach (var a in d.Children(op, "carry"))
        {
            var t = d.Text(a, "target")!; var src = d.Children(a, "value").Select(v => d.Text(v, "path")).FirstOrDefault() ?? "";
            if (!emitProps.TryGetValue(t, out var tp) || tp.Role != "telemetry") { Fail($"carry {t}: {t} is not a telemetry property of {Nm(emitT)}"); return null; }
            if (!edgeProps.TryGetValue(src, out var sp) || sp.Role != "telemetry") { Fail($"carry {t} = {src}: {src} is not a telemetry property of {Nm(edgeT)}"); return null; }
            if (tp.ValueKind != sp.ValueKind) { Fail($"carry {t} = {src}: {t} is {tp.ValueKind} and {src} is {sp.ValueKind}"); return null; }
            carry.Add((t, src));
        }
        var keeps = new List<(string Name, string End, string Prop)>();
        var imgProps = mapView.PropsOf(imgT).Keys.Select(p => mapView.Props[p]).ToDictionary(p => p.Name);
        foreach (var a in d.Children(op, "keep"))
        {
            var t = d.Text(a, "target")!; var path = d.Children(a, "value").Select(v => d.Text(v, "path")).FirstOrDefault() ?? "";
            var dot = path.IndexOf('.');
            if (dot < 0 || path[..dot] is not ("src" or "dst")) { Fail($"keep {t} = {path}: a kept value is src.property or dst.property"); return null; }
            if (!imgProps.TryGetValue(path[(dot + 1)..], out var kp) || kp.Role == "telemetry") { Fail($"keep {t} = {path}: {Nm(imgT)} has no data property {path[(dot + 1)..]}"); return null; }
            keeps.Add((t, path[..dot], path[(dot + 1)..]));
        }
        var derive = d.Children(op, "derive").Select(a => (Target: d.Text(a, "target")!, Node: d.Children(a, "value")[0])).ToList();
        var states = d.Children(op, "state").ToList();
        foreach (var (t, _) in derive)
            if (!emitProps.TryGetValue(t, out var tp) || tp.Role != "telemetry") { Fail($"derive {t}: {t} is not a telemetry property of {Nm(emitT)}"); return null; }
        // a telemetry property of the lifted relationship that the lower relationship also has, and that the statement does not name, is carried as it is
        foreach (var p in emitProps.Values.Where(p => p.Role == "telemetry" && !carry.Any(c => c.Target == p.Name) && !derive.Any(x => x.Target == p.Name) && edgeProps.TryGetValue(p.Name, out var ep) && ep.Role == "telemetry" && ep.ValueKind == p.ValueKind).OrderBy(p => p.Name, StringComparer.Ordinal))
            carry.Add((p.Name, p.Name));
        foreach (var (t, _) in carry)
            if (emitProps[t].Combine is not ("sum" or "min" or "max" or "mean")) { Fail($"carry {t}: the property declares combine {emitProps[t].Combine ?? "nothing"}; a lift combines with sum, min, max or mean"); return null; }
        foreach (var st in states)
        {
            var t = d.Text(st, "target")!; var of = d.Text(st, "of")!;
            if (!emitProps.TryGetValue(t, out var tp) || tp.Role == "telemetry") { Fail($"state {t}: {t} is not a property of {Nm(emitT)} (it holds the state, not a measure)"); return null; }
            if (!carry.Any(c => c.Target == of) && !derive.Any(x => x.Target == of)) { Fail($"state {t} of {of}: {of} is neither carried nor derived"); return null; }
        }
        if (keeps.Count > 0 && carry.Count == 0) { Fail("keep needs telemetry: the kept values ride on the windows"); return null; }
        if ((derive.Count > 0 || states.Count > 0) && carry.Count == 0) { Fail("derive and state need telemetry: no property of the lifted relationship is carried from the lower one"); return null; }
        var within = d.Int(op, "within", LiftDefaults.WithinNs);

        // text
        var imports = d.Out(Ids.Module(module), "imports").ToList();
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in imports) alias[Ids.ModuleOf(r.Target)] = r.Props.Text(Ids.KProp("alias")) ?? "";
        var extra = new List<(string Module, string Alias)>();
        string R(string typeId)
        {
            var m = Ids.ModuleOf(typeId);
            if (m == module) return Nm(typeId);
            if (!alias.TryGetValue(m, out var a)) { a = "__m" + extra.Count; alias[m] = a; extra.Add((m, a)); }
            return a + "." + Nm(typeId);
        }
        string ModRef(string m) { if (m == module) return m; if (!alias.TryGetValue(m, out var a)) { a = "__m" + extra.Count; alias[m] = a; extra.Add((m, a)); } return a; }
        var sb = new StringBuilder();
        var body = new StringBuilder();
        var L = name;
        var sameA = srcT == mapSrcT; var sameB = dstT == mapSrcT;
        body.Append($"  let {L}__edges = match {ePortName()} (source: {R(srcT)})-[edge: {R(edgeT)}]->(target: {R(dstT)});\n");
        var when = d.Children(op, "when") is { Count: > 0 } w ? Printer.ExprText(defs, module, w[0]) : null;
        body.Append($"  let {L}__images = match {mPortName()} (node: {R(mapSrcT)})-[:{R(mapT)}]->(image: {R(imgT)})" + (when is null ? "" : $"\n    |> where {when}") + ";\n");
        body.Append($"  let {L}__imagePairs = {L}__images |> select {{ m: node, i: image }};\n");
        string imageRows;
        if (images == "reject")
        {
            body.Append($"  let {L}__counted = {L}__imagePairs |> group by {{ m: m }} aggregate {{ n: count() }} |> select {{ mk: m, n: n }};\n");
            body.Append($"  let {L}__pairs = {L}__imagePairs;\n");
            body.Append($"  let {L}__joined = join {L}__pairs, {L}__counted on m == mk;\n");
            body.Append($"  let {L}__single = {L}__joined |> where n == 1;\n");
            imageRows = $"{L}__single";
        }
        else imageRows = $"{L}__imagePairs";
        body.Append($"  let {L}__from = {imageRows} |> select {{ sourceMapped: m, sourceImage: i }};\n");
        body.Append($"  let {L}__to = {imageRows} |> select {{ targetMapped: m, targetImage: i }};\n");
        string Eq(bool same, string x, string y) => same ? $"{x} == {y}" : $"entityId({x}) == entityId({y})";
        body.Append($"  let {L}__fromEnd = join {L}__edges, {L}__from on {Eq(sameA, "source", "sourceMapped")};\n");
        body.Append($"  let {L}__ends = join {L}__fromEnd, {L}__to on {Eq(sameB, "target", "targetMapped")};\n");
        body.Append(d.Text(op, "selfLoops") == "keep" ? $"  let {L}__lifted = {L}__ends;\n" : $"  let {L}__lifted = {L}__ends |> where sourceImage != targetImage;\n");
        body.Append($"  emit relationship {R(emitT)} from {L}__lifted key {L}: sourceImage -> targetImage;\n");
        if (carry.Count > 0)
        {
            var lower = carry.Select(c => c.Source).Distinct().ToList();
            body.Append($"  let {L}__windows = {L}__lifted\n    |> series {ePortName()} {string.Join(", ", lower.Select(x => "edge." + x))} within {Lexer.DurationText(within)}\n    |> rollup by {{ sourceImage: sourceImage, targetImage: targetImage{string.Concat(keeps.Select(k => $", {k.Name}: {(k.End == "src" ? "sourceImage" : "targetImage")}.{k.Prop}"))} }} combine {{ {string.Join(", ", carry.Select(c => $"{c.Target}: {emitProps[c.Target].Combine}({c.Source})"))} }};\n");
            var cols = new List<string> { "src: sourceImage", "dst: targetImage", "windowStart: windowStart" };
            cols.AddRange(keeps.Select(k => $"{k.Name}: {k.Name}"));
            cols.AddRange(carry.Select(c => $"{c.Target}: {c.Target}"));
            foreach (var (t, node) in derive) cols.Add($"{t}: {Printer.ExprText(defs, module, node)}");
            body.Append($"  let {L} = {L}__windows |> select {{ {string.Join(", ", cols)} }};\n");
            body.Append($"  emit update relationship {R(emitT)} from {L} key {L}: src -> dst set {{ {string.Join(", ", carry.Select(c => c.Target).Concat(derive.Select(x => x.Target)).Select(t => $"{t}: {t}"))} }};\n");
            var k = 0;
            foreach (var st in states)
            {
                var t = d.Text(st, "target")!;
                body.Append($"  let {L}__state{k} = {L} |> threshold {d.Text(st, "of")} by {{ src: src, dst: dst }} above {Norm.Decimal(d.Dec(st, "above"))} for {d.Int(st, "enterAfter")} then {Quote(d.Text(st, "high")!)} below {Norm.Decimal(d.Dec(st, "below"))} for {d.Int(st, "exitAfter")} then {Quote(d.Text(st, "low")!)} initial {Quote(d.Text(st, "initial")!)} as {t};\n");
                body.Append($"  emit update relationship {R(emitT)} from {L}__state{k} key {L}: src -> dst set {{ {t}: {t} }};\n");
                k++;
            }
        }
        else body.Append($"  let {L} = {L}__lifted |> select {{ src: sourceImage, dst: targetImage }};\n");

        // header: the module's own imports, the ports the statement reads, the projection
        var portNames = new[] { ePort, mPort }.Distinct().ToList();
        var portLines = portNames.Select(p => $"  in {d.Text(p, "name") ?? Nm(p)}: graph {ModRef(Expects(p)!)};\n").ToList();
        var outRef = outMod == module ? module : ModRef(outMod);
        var version = defs.Entities.FirstOrDefault(e => e.Id.StartsWith($"d:{module}@", StringComparison.Ordinal))?.Props.Text(Ids.KProp("version")) ?? "1.0.0";
        sb.Append($"module {module} @ {version};\n");
        foreach (var r in imports)
        {
            var im = Ids.ModuleOf(r.Target); var re = defs.Entity(r.Target);
            sb.Append($"import {im} @ {re?.Props.Text(Ids.KProp("version"))} {re?.Props.Text(Ids.KProp("digest"))} as {r.Props.Text(Ids.KProp("alias"))};\n");
        }
        foreach (var (m, a) in extra)
        {
            var re = defs.Entities.FirstOrDefault(e => e.Id.StartsWith($"d:{m}@", StringComparison.Ordinal));
            sb.Append($"import {m} @ {re?.Props.Text(Ids.KProp("version"))} {re?.Props.Text(Ids.KProp("digest"))} as {a};\n");
        }
        sb.Append($"projection {d.Text(pid, "name") ?? Nm(pid)} view forward output {outRef} {{\n");
        foreach (var l in portLines) sb.Append(l);
        if (d.Int(pid, "retain") > 0) sb.Append($"  retain {d.Int(pid, "retain")};\n");
        sb.Append(body);
        sb.Append("}\n");
        return sb.ToString();
    }

    static string Quote(string s) { var b = new StringBuilder(); Jcs.Str(b, s); return b.ToString(); }
}

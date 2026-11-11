// X2: is the lineage the engine stores (OutputBuilder.Supports, GroupMembers, GroupRefs, Lineage) a function of the input and the definitions?
// Engine A replays the capture as `sattle replay` does (4,000+ batches, chunk 2,000). Engine B evaluates the same rows from scratch with a different history:
// all batches of one source between two advances are merged into one batch, sources in a fixed order, so there are a few hundred batches and few retractions.
// Both engines share one interner, so element handles are comparable. The stored lineage of A and of B is compared for every derived element.
// usage: dotnet run -c Release -- CAPTURE_DIR DEFINITIONS_DIR OVERLAY_FILE
using System.Diagnostics;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

var capture = args[0]; var defs = args[1]; var overlay = args.Length > 2 && args[2] != "-" ? args[2] : null;
var files = Catalog.ReadDirectory(defs).ToList();
if (overlay is not null && File.Exists(overlay))
{
    var text = File.ReadAllText(overlay);
    var mod = System.Text.RegularExpressions.Regex.Match(text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
    var i = files.FindIndex(f => System.Text.RegularExpressions.Regex.Match(f.Text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m && m.Groups[1].Value == mod);
    files[i] = files[i] with { Text = text };
}
var cat = new Catalog(); var load = cat.Load(files, updatePins: true);
if (load.HasErrors) throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
var spaceId = cat.Spaces.Keys.First(); var cluster = Sattle.Cli.Program.DetectCluster(capture);
var it = new Interner();
Engine NewEngine() => new(cat, spaceId, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 }, it);

// ---- A: the reference replay, recording what the engine was fed
var events = new List<Ev>();
var a = NewEngine(); var swA = Stopwatch.StartNew();
{
    var rec = new Recorder(a, events);
    new Replay(rec, cat.Spaces[spaceId], AdapterRegistry.Create(cluster), new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, FlushAtEnd = true, Speed = 0, Cluster = cluster }).Run(capture);
}
var secsA = swA.Elapsed.TotalSeconds;
Console.WriteLine($"A: {events.Count(e => e.Kind == 'r'):N0} batches, {events.Count(e => e.Kind == 'a')} advances, {secsA:F1} s, digest {a.StateDigest()}");

// ---- B: merged batches per source between advances, sources in order of first appearance
var b = NewEngine(); var swB = Stopwatch.StartNew(); long bBatches = 0;
{
    var order = events.Where(e => e.Kind == 'r').Select(e => e.Source).Distinct().ToList();
    var pending = new Dictionary<string, List<Ev>>(); long seq = 1;
    void Flush()
    {
        foreach (var src in order) if (pending.TryGetValue(src, out var list) && list.Count > 0) { b.Ingest(src, Merge(b.RowLayout(src), list), seq++); bBatches++; list.Clear(); }
    }
    foreach (var ev in events)
    {
        if (ev.Kind == 'r') { if (!pending.TryGetValue(ev.Source, out var l)) pending[ev.Source] = l = []; l.Add(ev); }
        else if (ev.Kind == 'a') { Flush(); b.Advance(ev.Value, seq++); }
        else { Flush(); b.RecordLoss(ev.Seq, ev.Value, ev.Reason); }
    }
    Flush();
}
var secsB = swB.Elapsed.TotalSeconds;
var digestEqual = a.StateDigest() == b.StateDigest();
Console.WriteLine($"B: {bBatches:N0} merged batches, {secsB:F1} s, digest {b.StateDigest()}; state digests equal: {digestEqual}");

// ---- compare the stored lineage per graph
long total = 0, supEqual = 0, supDiff = 0, onlyA = 0, onlyB = 0, grpDiff = 0, linDiffSinkRule = 0, grefDiff = 0;
long linOnlyA = 0, linOnlyAStale = 0, linOnlyB = 0; var diffExamples = new List<string>(); var grefEx = new List<string>(); var linEx = new List<string>(); var perGraph = new List<string>();
var sample = new List<(string Graph, int Elem)>(); var allPool = new List<(string Graph, int Elem)>(); var rnd = new Random(20261111);
foreach (var ga in a.Graphs)
{
    var gb = b.Graph(ga.Name); long n = 0, d = 0;
    var oa = ga.Out; var ob = gb.Out;
    var elems = oa.Supports.Keys.Union(ob.Supports.Keys).ToList();
    foreach (var el in elems)
    {
        n++; var inA = oa.Supports.TryGetValue(el, out var sa); var inB = ob.Supports.TryGetValue(el, out var sb);
        if (inA && inB) { if (Same(sa!, sb!)) supEqual++; else { supDiff++; d++; if (diffExamples.Count < 5) diffExamples.Add($"{ga.Name} {it.Get(el)}: A {sa!.Count} rows, B {sb!.Count} rows"); } }
        else if (inA) { onlyA++; d++; } else { onlyB++; d++; }
    }
    // group members and refs
    foreach (var (grp, byKey) in oa.GroupMembers)
    {
        if (!ob.GroupMembers.TryGetValue(grp, out var kb)) { grpDiff += byKey.Count; continue; }
        foreach (var (k, rows) in byKey) if (!kb.TryGetValue(k, out var rb) || !Same(rows, rb)) grpDiff++;
        grpDiff += kb.Keys.Count(k => !byKey.ContainsKey(k));
    }
    foreach (var (grp, kb) in ob.GroupMembers) if (!oa.GroupMembers.ContainsKey(grp)) grpDiff += kb.Count;
    foreach (var (el, r) in oa.GroupRefs) if (!ob.GroupRefs.TryGetValue(el, out var r2) || r2 != r) { grefDiff++; if (grefEx.Count < 4) grefEx.Add($"{ga.Name} {it.Get(el)}: A {r} B {(ob.GroupRefs.TryGetValue(el, out var q) ? q.ToString() : "none")}"); }
    grefDiff += ob.GroupRefs.Keys.Count(el => !oa.GroupRefs.ContainsKey(el));
    // the one-row lineage: sink, rule and number of bound elements must match; its sources must be among the supports
    foreach (var (el, la) in oa.Lineage)
    {
        if (!ob.Lineage.TryGetValue(el, out var lb)) { linDiffSinkRule++; linOnlyA++; if (!ga.State.Entities.Any(x => it.TryFind(x.Id, out var h) && h == el) && ga.State.Rel(it.Get(el)) is null) linOnlyAStale++; continue; }
        if (la.Sink != lb.Sink || la.Rule != lb.Rule || la.Total != lb.Total) { linDiffSinkRule++; if (linEx.Count < 4) linEx.Add($"{ga.Name} {it.Get(el)}: A {la.Sink}/{la.Rule}/{la.Total} B {lb.Sink}/{lb.Rule}/{lb.Total}"); }
    }
    var onlyBn = ob.Lineage.Keys.Count(el => !oa.Lineage.ContainsKey(el)); linDiffSinkRule += onlyBn; linOnlyB += onlyBn;
    total += n; perGraph.Add($"{ga.Name} | {n:N0} | {d:N0}");
    var pool = oa.Supports.Keys.ToList(); allPool.AddRange(pool.Select(e => (ga.Name, e)));
    if (ga.Name != "master") sample.AddRange(pool.OrderBy(_ => rnd.Next()).Take(Math.Min(pool.Count, 50)).Select(e => (ga.Name, e)));
}
Console.WriteLine("graph | elements with recorded supports (union of A and B) | elements that differ");
foreach (var p in perGraph) Console.WriteLine(p);
Console.WriteLine($"elements with recorded supports: {total:N0}; equal {supEqual:N0}; different row sets {supDiff:N0}; only in A {onlyA:N0}; only in B {onlyB:N0}");
Console.WriteLine($"group members: differing groups {grpDiff:N0}; group refs differing {grefDiff:N0}; one-row lineage with a different sink, rule or arity {linDiffSinkRule:N0}");
Console.WriteLine($"one-row lineage entries only in A: {linOnlyA} (of which the element is no longer in the graph: {linOnlyAStale}); only in B: {linOnlyB}; different content: {linDiffSinkRule - linOnlyA - linOnlyB}");
foreach (var x in diffExamples.Concat(grefEx).Concat(linEx)) Console.WriteLine("  " + x);
// the stratified sample of the procedure: 50 per graph or all, then capped at 1,000
var pick = sample.Concat(allPool.Except(sample).OrderBy(_ => rnd.Next())).Take(1000).ToList(); long eq = 0;
foreach (var (g, el) in pick) { var oa = a.Graph(g).Out; var ob = b.Graph(g).Out; if (ob.Supports.TryGetValue(el, out var sb) && Same(oa.Supports[el], sb)) eq++; }
Console.WriteLine($"sample of {pick.Count} elements (seed 20261111, at least 50 or all per graph with lineage, then uniform): supports equal for {eq} of {pick.Count}");
a.Dispose(); b.Dispose();

static bool Same(Dictionary<string, long> x, Dictionary<string, long> y) => x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);

static Batch Merge(Layout layout, List<Ev> list)
{
    var n = list.Sum(e => e.Batch!.N); var m = new Batch(layout, n) { N = n };
    var anyW = list.Any(e => e.Batch!.W is not null); if (anyW) m.W = new long[n];
    var anyP = new bool[layout.Count]; foreach (var e in list) for (var c = 0; c < layout.Count; c++) if (e.Batch!.Cols[c].P is not null) anyP[c] = true;
    for (var c = 0; c < layout.Count; c++) if (anyP[c]) { m.Cols[c].P = new bool[n]; Array.Fill(m.Cols[c].P!, true); }
    var o = 0;
    foreach (var e in list)
    {
        var s = e.Batch!;
        for (var c = 0; c < layout.Count; c++)
        {
            if (layout.IsDec[c]) Array.Copy(s.Cols[c].D, 0, m.Cols[c].D, o, s.N); else Array.Copy(s.Cols[c].L, 0, m.Cols[c].L, o, s.N);
            if (s.Cols[c].P is not null) Array.Copy(s.Cols[c].P!, 0, m.Cols[c].P!, o, s.N);
        }
        if (anyW) for (var i = 0; i < s.N; i++) m.W![o + i] = s.Weight(i);
        o += s.N;
    }
    return m;
}

sealed record Ev(char Kind, string Source, Batch? Batch, long Seq, long Value, string Reason);
sealed class Recorder(Engine e, List<Ev> events) : IEngineInput
{
    public Interner Interner => e.Interner;
    public Layout RowLayout(string source) => e.RowLayout(source);
    public void Ingest(string source, Batch rows, long seq) { events.Add(new Ev('r', source, Copy(rows), seq, 0, "")); e.Ingest(source, rows, seq); }
    public void Advance(long lambdaNs, long seq) { events.Add(new Ev('a', "", null, seq, lambdaNs, "")); e.Advance(lambdaNs, seq); }
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") { events.Add(new Ev('l', "", null, timeNs, count, reason)); e.RecordLoss(timeNs, count, reason); }
    static Batch Copy(Batch b)
    {
        var c = new Batch(b.Layout, b.N) { N = b.N, W = b.W is null ? null : b.W[..b.N] };
        for (var i = 0; i < b.Cols.Length; i++)
        {
            if (b.Layout.IsDec[i]) Array.Copy(b.Cols[i].D, c.Cols[i].D, b.N); else Array.Copy(b.Cols[i].L, c.Cols[i].L, b.N);
            c.Cols[i].P = b.Cols[i].P is null ? null : b.Cols[i].P![..b.N];
        }
        return c;
    }
}

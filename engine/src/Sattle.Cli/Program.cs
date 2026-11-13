using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Cli;

public static class Program
{
    const string Usage = """
        sattle check  [--defs DIR] [--update-pins] [--print MODULE] [--plan]
        sattle replay CAPTURE [--defs DIR] [--overlay FILE] [--space NAME] [--cluster NAME] [--speed N] [--advance-every 60s] [--lateness 60s] [--late-horizon WINDOWS] [--chunk N]
                              [--store memory|sqlite=PATH|cosmos=ENDPOINT[,db=NAME][,drop]] [--until RFC3339] [--out DIR] [--projections sattle|kql]
        sattle serve  CAPTURE [replay options] [--port 5080] [--write-definitions] [--store sqlite=PATH|cosmos=ENDPOINT[,db=NAME]] [--seal-every ENTRIES] [--verify-journal] [--retain-patches N] [--retain-events N]
        sattle serve  --store sqlite=PATH|cosmos=ENDPOINT[,db=NAME] [--port 5080]       (restart from the journal of the store, no capture needed)
        sattle serve  --live [--eh-namespace NS] [--hub sattle-ingest] [--group $Default] [--from checkpoint|latest|earliest] [--seconds 300] [--live-out DIR] [--cluster NAME]
        sattle bench  [--rows N] [--pods N] [--services N] [--windows N]
        sattle bench  estate | b5 [--store memory|sqlite] | b7 [--cosmos ENDPOINT] | b8 | b10   [--capture DIR|latest] [--runs 10]
        CAPTURE is a capture directory, lab/fixtures, or "latest" (newest under lab/captures).
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            var o = Options.Parse(args[1..]);
            return args[0] switch
            {
                "check" => Check(o),
                "replay" => Replay(o, serve: false),
                "serve" => Replay(o, serve: true),
                "bench" => Bench.Run(o),
                _ => Fail($"unknown command '{args[0]}'\n{Usage}"),
            };
        }
        catch (ArgumentException e) { return Fail(e.Message); }
    }

    static int Fail(string msg) { Console.Error.WriteLine(msg); return 2; }

    // ---- shared ----

    public sealed class Options
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
        static readonly HashSet<string> FlagNames = ["update-pins", "plan", "no-flush", "trace-alloc", "live", "write-definitions", "no-seal", "verify-journal", "no-tree", "census", "export"];
        public static Options Parse(string[] a)
        {
            var o = new Options();
            for (var i = 0; i < a.Length; i++)
            {
                if (!a[i].StartsWith("--", StringComparison.Ordinal)) { o.Positional.Add(a[i]); continue; }
                var k = a[i][2..];
                if (FlagNames.Contains(k)) { o.Flags.Add(k); continue; }
                if (i + 1 >= a.Length) throw new ArgumentException($"--{k} needs a value");
                o.Named[k] = a[++i];
            }
            return o;
        }
        public string? Get(string k) => Named.GetValueOrDefault(k);
        public long Duration(string k, long dflt)
        {
            if (!Named.TryGetValue(k, out var v)) return dflt;
            try { return Lexer.DurationNs(v); } catch (Exception) { throw new ArgumentException($"--{k}: expected a duration like 60s, got '{v}'"); }
        }
    }

    static string FindDefinitions(Options o)
    {
        if (o.Get("defs") is { } d) return d;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var c = Path.Combine(dir.FullName, "definitions");
                if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.sattle").Any()) return c;
                c = Path.Combine(dir.FullName, "engine", "definitions");
                if (Directory.Exists(c)) return c;
            }
        throw new ArgumentException("definitions directory not found; pass --defs DIR");
    }

    /// <summary>The definitions directory, with `--overlay FILE[,FILE]` replacing the module of the same name (a different space binding for a synthetic capture, for example). Committed edits are written back to the original files, not to an overlay.</summary>
    public static IReadOnlyList<SourceFile> ReadDefinitions(Options o)
    {
        var files = Catalog.ReadDirectory(FindDefinitions(o)).ToList();
        var overlay = o.Get("overlay") ?? LocalCaptureOverlay(o);
        if (overlay is null)
        {
            // a capture that says which workload it holds: the tracked space with those values
            string? dir = null; if (o.Positional.Count > 0) try { dir = FindCapture(o.Positional[0]); } catch (ArgumentException) { }
            return dir is not null && ArgumentsOf(dir) is { } arguments ? WithArguments(files, arguments) : files;
        }
        foreach (var path in overlay.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = File.ReadAllText(path);
            var module = System.Text.RegularExpressions.Regex.Match(text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : throw new ArgumentException($"overlay {path} has no module header");
            var i = files.FindIndex(f => System.Text.RegularExpressions.Regex.Match(f.Text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } fm && fm.Groups[1].Value == module);
            if (i < 0) throw new ArgumentException($"no module {module} in the definitions to overlay");
            files[i] = files[i] with { Text = text };
        }
        return files;
    }

    /// <summary>The definition files whose text is not the text of their file: a space bound with a capture's arguments, a module replaced by an overlay. A commit writes every definition back, so with `--write-definitions` these would put a capture's values into the definitions folder.</summary>
    public static List<string> NotAsOnDisk(IReadOnlyList<SourceFile> files) =>
        [.. files.Where(f => !File.Exists(f.Path) || File.ReadAllText(f.Path) != f.Text).Select(f => Path.GetFileName(f.Path))];

    /// <summary>The file a capture directory may hold beside its data: the values of the arguments of the space for that capture, by name (`{ "workloadNamespace": "...", ... }`). `lab/fixtures/build.sh` writes it for the fixtures.</summary>
    public const string SpaceArguments = "space-arguments.json";

    /// <summary>The arguments file of a capture directory, or null when it has none.</summary>
    public static IReadOnlyDictionary<string, string>? ArgumentsOf(string captureDir)
    {
        var path = Path.Combine(captureDir, SpaceArguments);
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }

    /// <summary>The text of a space with other values for its arguments: every `name: "value"` whose name is given takes the given value. A name the text does not have changes nothing, an argument that is not given keeps its value.</summary>
    public static string WithArguments(string spaceText, IReadOnlyDictionary<string, string> arguments) =>
        arguments.OrderBy(a => a.Key, StringComparer.Ordinal).Aggregate(spaceText, (text, a) =>
            System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(a.Key)}: ""(?:[^""\\]|\\.)*""", _ => $"{a.Key}: {JsonSerializer.Serialize(a.Value)}"));

    /// <summary>The definitions with the arguments applied to the modules that declare a space.</summary>
    public static List<SourceFile> WithArguments(IReadOnlyList<SourceFile> files, IReadOnlyDictionary<string, string> arguments) =>
        [.. files.Select(f => System.Text.RegularExpressions.Regex.IsMatch(f.Text, @"^space\s", System.Text.RegularExpressions.RegexOptions.Multiline) ? f with { Text = WithArguments(f.Text, arguments) } : f)];

    /// <summary>The space binding for a capture that the tracked `acme.space` does not fit, applied when no `--overlay` was given. For a capture under `lab/captures` it is the gitignored `lab/captures/space-overlay.sattle` (the real workload arguments, never committed). For any other capture directory NAME it is the tracked `engine/overlays/NAME.space.sattle` when it exists (`lab/fixtures` has `fixtures.space.sattle`): the tracked space file binds the invented estate, so a capture of another cluster needs its own arguments.</summary>
    static string? LocalCaptureOverlay(Options o)
    {
        if (o.Positional.Count == 0) return null;
        string dir; try { dir = FindCapture(o.Positional[0]); } catch (ArgumentException) { return null; }
        var full = Path.GetFullPath(dir.TrimEnd('/'));
        var parent = Path.GetDirectoryName(full);
        if (Path.GetFileName(parent) == "captures")
        {
            var local = Path.Combine(parent!, "space-overlay.sattle");
            return File.Exists(local) ? local : null;
        }
        return null;
    }

    /// <summary>The captures in a captures folder, oldest first: the directories named by their capture time (`20261110T105738Z`). Any other directory there is work in progress of the lab and no capture.</summary>
    public static IReadOnlyList<string> Captures(string folder) => !Directory.Exists(folder) ? [] :
        [.. Directory.EnumerateDirectories(folder).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), @"^\d{8}T\d{6}Z$")).Order(StringComparer.Ordinal)];

    internal static string FindCapture(string arg)
    {
        if (arg != "latest") return arg;
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var c = Path.Combine(dir.FullName, "lab", "captures");
            if (Directory.Exists(c)) return Captures(c).LastOrDefault() ?? throw new ArgumentException($"no capture under {c}");
        }
        throw new ArgumentException("lab/captures not found");
    }

    static void Print(IEnumerable<Diagnostic> diags) { foreach (var d in diags) Console.Error.WriteLine(d); }

    // ---- check ----

    static int Check(Options o)
    {
        var dir = FindDefinitions(o);
        var files = Catalog.ReadDirectory(dir);
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: o.Flags.Contains("update-pins"));
        if (o.Flags.Contains("update-pins") && res.PinFixes.Count > 0)
        {
            foreach (var (path, text) in Catalog.ApplyPinFixes(files, res.PinFixes)) File.WriteAllText(path, text);
            Console.WriteLine($"updated {res.PinFixes.Count} import pin(s) in {res.PinFixes.Select(f => f.File).Distinct().Count()} file(s)");
            files = Catalog.ReadDirectory(dir);
            cat = new Catalog(); res = cat.Load(files);
        }
        Print(res.Diagnostics);
        var errors = res.Diagnostics.Count(d => d.IsError);
        Console.WriteLine($"{files.Count} file(s), {cat.Modules.Count - 1} module(s), {cat.Projections.Count} projection(s), {cat.Spaces.Count} graph space(s), catalog revision {cat.Log.Head}, {errors} error(s), {res.Diagnostics.Count - errors} warning(s)");
        foreach (var m in cat.Modules.Values.Where(m => m.Name != Ids.KernelModule)) Console.WriteLine($"  {m.Name} @ {m.Version} {m.Digest}");
        if (o.Get("print") is { } mod)
        {
            if (!cat.Modules.ContainsKey(mod)) return Fail($"module {mod} is not loaded");
            Console.Write(Printer.Print(cat.Graph, mod));
        }
        if (o.Flags.Contains("plan") && errors == 0)
            foreach (var sp in cat.Spaces.Keys)
            {
                using var e = new Engine(cat, sp);
                var bad = AdapterRegistry.Verify(cat, cat.Spaces[sp], AdapterRegistry.Create("c"));
                Print(bad); errors += bad.Count(x => x.IsError);
                foreach (var g in e.Graphs)
                {
                    Console.WriteLine($"plan {g.Name} ({g.Plan.Info.Projection}) digest {g.Plan.Info.Digest}");
                    foreach (var s in g.Plan.Info.Strategies) Console.WriteLine("  " + s);
                    foreach (var c in g.Plan.Info.FusedChains) Console.WriteLine("  fused: " + string.Join(" > ", c));
                    Console.WriteLine("  memory: " + g.Plan.Info.MemoryBound);
                }
            }
        return errors == 0 ? 0 : 1;
    }

    // ---- replay and serve ----

    public static string DetectCluster(string dir)
    {
        var f = Directory.EnumerateFiles(dir, "flows.ndjson", SearchOption.AllDirectories).FirstOrDefault(x => !x.Contains("eventhub"));
        if (f is null) return "lab";
        foreach (var line in File.ReadLines(f).Take(50))
        {
            try
            {
                using var d = JsonDocument.Parse(line);
                var c = d.RootElement.Path("payload", "flow", "source", "cluster_name") ?? d.RootElement.Path("payload", "flow", "destination", "cluster_name");
                if (c is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } v) return v;
            }
            catch (JsonException) { /* skip */ }
        }
        return "lab";
    }

    /// <summary>`--store cosmos=<endpoint>[,db=<database>][,drop]`: Entra authentication for an account, the emulator's well-known key only for a localhost endpoint. `drop` deletes the database when the run ends.</summary>
    static CosmosGraphStore? cosmos;
    static CosmosGraphStore OpenCosmos(string spec)
    {
        var parts = spec.Split(',');
        string? Opt(string k) => parts.Skip(1).Select(x => x.Split('=', 2)).Where(x => x[0] == k && x.Length == 2).Select(x => x[1]).FirstOrDefault();
        return cosmos = CosmosGraphStore.Open(parts[0], Opt("db") ?? "sattle", parts.Skip(1).Contains("drop"));
    }

    static int Replay(Options o, bool serve)
    {
        var live = serve && o.Flags.Contains("live");
        var resumeOnly = serve && !live && o.Positional.Count == 0 && o.Get("store") is { } sspec && sspec != "memory";   // serve --store X with no capture: the journal in X is all there is
        if (o.Positional.Count == 0 && !live && !resumeOnly) return Fail("a capture directory is needed\n" + Usage);
        var capture = live || resumeOnly ? "" : FindCapture(o.Positional[0]);
        if (!live && !resumeOnly && !Directory.Exists(capture)) return Fail($"capture directory {capture} does not exist");
        if (resumeOnly) return Serve(o, "", o.Get("cluster") ?? "lab");
        var files = ReadDefinitions(o);
        if (serve && o.Flags.Contains("write-definitions") && NotAsOnDisk(files) is { Count: > 0 } bound)
            return Fail($"--write-definitions is refused: {string.Join(", ", bound)} would be written with the values of the capture's {SpaceArguments} or of an overlay, not as the file is. Serve without --write-definitions, or without the arguments file and the overlay.");
        if (o.Get("projections") is { } front && front != "sattle")
        {
            if (front != "kql") return Fail($"--projections {front}: sattle or kql");
            if (serve) return Fail("--projections kql is for replay only: serve writes definition edits back to the .sattle files");
            var put = Sattle.Kql.KqlFiles.Of(files, Sattle.Kql.KqlFiles.ReadDirectory(FindDefinitions(o)));
            if (!put.Ok) { Print(put.Diagnostics); return 1; }
            files = put.Files;
        }
        var cat = new Catalog();
        var load = cat.Load(files, updatePins: true);
        if (load.HasErrors) { Print(load.Diagnostics); return 1; }
        var spaceId = o.Get("space") is { } sn ? cat.Spaces.Keys.FirstOrDefault(k => Ids.Name(k) == sn) ?? throw new ArgumentException($"no graph space '{sn}'") : cat.Spaces.Keys.First();
        string cluster;
        if (o.Get("cluster") is { } cl) cluster = cl;
        else if (live) { try { cluster = DetectCluster(FindCapture("latest")); } catch (ArgumentException) { cluster = "lab"; } }
        else cluster = DetectCluster(capture);
        if (serve) return Serve(o, capture, cluster);
        var adapters = AdapterRegistry.Create(cluster);
        var bad = AdapterRegistry.Verify(cat, cat.Spaces[spaceId], adapters);
        if (bad.Count > 0) { Print(bad); return 1; }
        var store = o.Get("store") ?? "memory";
        var opts = new EngineOptions
        {
            Tenant = "lab",
            LateHorizonWindows = LateHorizonFor(o),   // a replay is finite: keep every window revisable so history is not dropped as too late (a live source keeps 3; `serve` of a capture uses the same value as `replay`)
            StoreFactory = _ => store.StartsWith("sqlite=", StringComparison.Ordinal) ? new SqliteGraphStore(store["sqlite=".Length..]) : store.StartsWith("cosmos=", StringComparison.Ordinal) ? OpenCosmos(store["cosmos=".Length..]) : new HeadOnlyGraphStore(),
        };
        var engine = new Engine(cat, spaceId, opts);
        engine.TrackAllocations = o.Flags.Contains("trace-alloc");
        var export = new CanvasExport(engine, cat);
        export.Attach();
        var ropts = ReplayOptionsOf(o, cluster);
        var replay = new Replay(engine, cat.Spaces[spaceId], adapters, ropts);
        Console.WriteLine($"capture {Path.GetFileName(capture.TrimEnd('/'))}, space {Ids.Name(spaceId)}, cluster {cluster}, store {store}, speed {(ropts.Speed > 0 ? ropts.Speed + "x" : "max")}");
        var stats = replay.Run(capture);
        Report(engine, cat, stats, adapters);
        if (engine.TrackAllocations) { Console.WriteLine("operator kinds over the replay:"); Bench.KindTable(engine, Console.Out); }
        if (o.Flags.Contains("census")) Bench.GcAndCensus(engine, Console.Out, ("canvas export", export), ("catalog", cat), ("adapters", adapters), ("replay", replay), ("replay stats", stats));
        if (o.Get("out") is { } outDir) { export.WriteFiles(outDir, cat.Spaces[spaceId]); Console.WriteLine($"wrote canvas files to {outDir}"); }
        engine.Dispose();
        return 0;
    }

    public static ReplayOptions ReplayOptionsOf(Options o, string cluster) => new()
    {
        ChunkRows = (int)(long.TryParse(o.Get("chunk"), out var ch) ? ch : 2000),
        AdvanceEveryNs = o.Duration("advance-every", 60_000_000_000), LatenessNs = o.Duration("lateness", 60_000_000_000),
        FlushAtEnd = !o.Flags.Contains("no-flush"), Speed = double.TryParse(o.Get("speed"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) ? sp : 0,
        Cluster = cluster, UntilNs = o.Get("until") is { } u && Norm.TryParseInstant(u, out var un) ? un : null,
    };

    static void Report(Engine e, Catalog cat, ReplayStats s, IReadOnlyList<ISourceAdapter> adapters)
    {
        Console.WriteLine($"replayed {s.Records.Values.Sum():N0} record(s) in {s.Elapsed.TotalSeconds:F1} s, capture span {(s.LastCaptureNs - s.FirstCaptureNs) / 1e9:F0} s, {s.Advances} logical-time advance(s), {s.Chunks} chunk(s)");
        Console.WriteLine("records by source: " + string.Join(", ", s.Records.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:N0}")) + (s.Skipped > 0 ? $" ({s.Skipped:N0} not consumed: no adapter in this slice)" : ""));
        Console.WriteLine("rows by source:    " + string.Join(", ", s.Rows.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value:N0}")));
        Console.WriteLine($"rejected records: {s.Rejected}" + (s.RejectionSamples.Count > 0 ? " e.g. " + string.Join("; ", s.RejectionSamples.Take(3)) : ""));
        var hub = adapters.OfType<HubbleAdapter>().First();
        Console.WriteLine($"hubble: {hub.Flows:N0} flows, {hub.LostEventRecords} lost-event record(s) ({hub.LostEvents:N0} events lost)");
        var k8s = adapters.OfType<KubernetesAdapter>().First();
        Console.WriteLine($"kubernetes events consumed: {k8s.Events:N0}, azure resources: {adapters.OfType<AzureResourceGraphAdapter>().First().Resources:N0}, prometheus samples: {adapters.OfType<PrometheusAdapter>().First().Samples:N0}");
        foreach (var g in e.Graphs)
        {
            var byType = g.State.Entities.GroupBy(x => Ids.Name(x.Type)).Select(x => $"{x.Key}={x.Count():N0}")
                .Concat(g.State.Rels.GroupBy(x => Ids.Name(x.Type)).Select(x => $"{x.Key}={x.Count():N0}")).OrderBy(x => x, StringComparer.Ordinal);
            var tel = e.Telemetry is InMemoryTelemetryStore mem ? mem.Dump(g.Key) : [];
            var partial = e.Telemetry is InMemoryTelemetryStore m2 ? m2.PartialWindows(g.Key).Count : 0;
            Console.WriteLine($"graph {g.Name}: revision {g.Head:N0}, {g.State.EntityCount:N0} entities, {g.State.RelCount:N0} relationships, telemetry samples {tel.Count:N0} ({tel.Select(x => (x.Element, x.Ws)).Distinct().Count():N0} element-windows, {partial} partial window(s))");
            Console.WriteLine("  " + string.Join(", ", byType));
        }
        var tree = e.Graphs.FirstOrDefault(g => g.Name == "serviceTree");
        if (tree is not null)
        {
            var idToName = tree.State.Entities.ToDictionary(x => x.Id, x => x.Props.Text("d:acme.svc/prop:name") ?? x.Props.Text("d:acme.svc/prop:namespace") ?? x.Id);
            var tel = ((InMemoryTelemetryStore)e.Telemetry).Dump(tree.Key);
            var flows = tel.Where(x => e.Interner.Get(x.Prop) == "d:acme.svc/prop:flows").GroupBy(x => x.Element).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
            var top = tree.State.Rels.Where(r => Ids.Name(r.Type) == "DependsOn").Select(r => (Rel: r, Flows: flows.GetValueOrDefault(e.Interner.TryFind(r.Id, out var h) ? h : -1))).OrderByDescending(x => x.Flows).Take(5).ToList();
            Console.WriteLine($"service tree: {tree.State.Entities.Count(x => Ids.Name(x.Type) == "ServiceGroup"):N0} group(s), {tree.State.Entities.Count(x => Ids.Name(x.Type) == "ServiceNode"):N0} service(s), {tree.State.Rels.Count(r => Ids.Name(r.Type) == "DependsOn"):N0} dependencies, {tree.State.Rels.Count(r => Ids.Name(r.Type) == "Contains"):N0} containment edges");
            foreach (var (rel, f) in top) Console.WriteLine($"  top dependency {idToName.GetValueOrDefault(rel.Source, rel.Source)} -> {idToName.GetValueOrDefault(rel.Target, rel.Target)}: {f:N0} flows, status {rel.Props.Text("d:acme.svc/prop:status") ?? "-"}");
        }
        if (cosmos is not null) foreach (var (kind, (calls, ru)) in cosmos.Charges.OrderBy(x => x.Key)) Console.WriteLine($"cosmos {kind}: {calls:N0} request(s), charge {ru:N1}");
        Console.WriteLine($"identities: {e.Identities.Minted:N0} minted, {e.Identities.Aliased:N0} aliased, {e.Identities.Merges:N0} merged, {e.Identities.Conflicts:N0} conflict(s); runtime faults: {e.Faults.DivisionByZero} division by zero, {e.Faults.Overflow} overflow or canonicalization failure, {e.Faults.InvalidParameter} row(s) with threshold parameters that are no state machine; late rows dropped {e.Graphs.Sum(g => g.Cx.LateDropped)}, rejected {e.Graphs.Sum(g => g.Cx.LateRejected)}; skipped emits {e.Graphs.Sum(g => g.Cx.SkippedEmits):N0}, uniqueness rejections {e.Graphs.Sum(g => g.Out.Rejections)}");
        Console.WriteLine($"merges: {e.Identities.Merges:N0} in {e.EpochsWithMerge:N0} epoch(s) of {e.Epochs:N0}, at most {e.MostMergesInOneEpoch:N0} in one; telemetry windows written under two ids of one class {e.Graphs.Sum(g => g.Out.WindowsOfTwoIds):N0}, of them without a value by a merge conflict {e.Graphs.Sum(g => g.Out.MergeConflicts):N0}");
        foreach (var (g, fp, gained) in e.FixpointsAtBound()) Console.WriteLine($"fixpoint at its bound: {g} {fp}, the last step added {gained} fact(s); facts that need more steps are missing");
        Console.WriteLine($"conflicts: {e.Graphs.Sum(g => g.Out.PropertyConflicts):N0} property, {e.Graphs.Sum(g => g.Out.ElementConflicts):N0} element, {e.Graphs.Sum(g => g.Out.WindowConflicts) + (e.Telemetry is InMemoryTelemetryStore merged ? e.Graphs.Sum(g => merged.MergeConflicts(g.Key)) : 0):N0} telemetry window, {e.Graphs.Sum(g => g.Out.Rejections):N0} uniqueness" + string.Concat(e.Graphs.Where(g => g.Out.PropertyConflicts + g.Out.WindowConflicts + g.Out.Rejections > 0).Select(g => $"; {g.Name} {g.Out.PropertyConflicts}/{g.Out.WindowConflicts}/{g.Out.Rejections}")));
        Console.WriteLine("state digest " + e.StateDigest());
    }

    /// <summary>The late horizon of a run: `--late-horizon` when given, 3 for a live source (it must bound its state), none for a recorded capture, whether it is replayed or served: serving a capture is a replay.</summary>
    public static int? LateHorizonFor(Options o) => int.TryParse(o.Get("late-horizon"), out var lh) ? lh : o.Flags.Contains("live") ? 3 : null;

    // ---- serve ----

    /// <summary>The options `serve` builds its host with.</summary>
    public static Sattle.Runtime.HostOptions HostOptionsFor(Options o, string cluster, IGraphStore? durable) =>
        new Sattle.Runtime.HostOptions { Cluster = cluster, LateHorizonWindows = LateHorizonFor(o), SpaceName = o.Get("space"), Durable = durable, SealEvery = long.TryParse(o.Get("seal-every"), out var se) ? se : durable is null ? 0 : 5000, UseSeal = !o.Flags.Contains("verify-journal"), VerifyJournal = o.Flags.Contains("verify-journal"), RetainPatches = int.TryParse(o.Get("retain-patches"), out var rp) ? rp : 2_000, RetainEvents = int.TryParse(o.Get("retain-events"), out var re) ? re : 20_000 };

    static int Serve(Options o, string capture, string cluster)
    {
        IReadOnlyList<SourceFile>? files = null;
        try { files = ReadDefinitions(o); } catch (ArgumentException) when (o.Get("store") is { } && o.Get("store") != "memory") { /* a store with a journal needs no definitions directory */ }
        var spec = o.Get("store");
        IGraphStore? durable = spec is null || spec == "memory" ? null : spec.StartsWith("sqlite=", StringComparison.Ordinal) ? new SqliteGraphStore(spec["sqlite=".Length..]) : spec.StartsWith("cosmos=", StringComparison.Ordinal) ? OpenCosmos(spec["cosmos=".Length..]) : throw new ArgumentException($"unknown store '{spec}'");
        var hopts = HostOptionsFor(o, cluster, durable);
        var host = durable is null ? new SpaceHost(files!, hopts) : SpaceHost.OpenDurable(durable, files, hopts);
        host.DefinitionsDir = files is null ? null : FindDefinitions(o); host.WriteDefinitions = o.Flags.Contains("write-definitions");
        if (host.Restored) Console.WriteLine($"restored from {spec}: {host.RestoreSummary()}. Sequence {host.Seq}, catalog revision {host.CatalogRevision}, state digest {host.StateDigest()}");
        return Server.Run(host, o, capture, host.Cluster, files ?? []);
    }
}

static class JsonExt
{
    public static JsonElement? P(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    public static JsonElement? Path(this JsonElement e, params string[] path) { JsonElement? cur = e; foreach (var p in path) { cur = cur?.P(p); if (cur is null) return null; } return cur; }
}


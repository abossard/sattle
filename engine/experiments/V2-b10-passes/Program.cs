// V2: what is in a `bench b10 --hours N` history when N is above the capture span (review 06, second point)?
// Repeats the b10 loop (Bench.Latency.cs:289-300) on an Engine with the host default late horizon (3 windows) and prints, per pass, the rows per source,
// the engine's late-dropped counters and the size of the master.
// usage: dotnet run -c Release -- CAPTURE_DIR DEFINITIONS_DIR OVERLAY_FILE PASSES
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

var capture = args[0]; var defs = args[1]; var overlay = args[2]; var passes = int.Parse(args[3]); const long Min = 60_000_000_000;
var files = Catalog.ReadDirectory(defs).ToList();
var text = File.ReadAllText(overlay);
var mod = System.Text.RegularExpressions.Regex.Match(text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
var ix = files.FindIndex(f => System.Text.RegularExpressions.Regex.Match(f.Text, @"^module\s+(\S+?)\s*(?:@|;)", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m && m.Groups[1].Value == mod);
files[ix] = files[ix] with { Text = text };
var cat = new Catalog(); var load = cat.Load(files, updatePins: true); if (load.HasErrors) throw new InvalidOperationException(string.Join("\n", load.Diagnostics));
var spaceId = cat.Spaces.Keys.First(); var cluster = Sattle.Cli.Program.DetectCluster(capture);
using var e = new Engine(cat, spaceId, new EngineOptions { Tenant = "lab", LateHorizonWindows = 3 });      // HostOptions and EngineOptions default, as bench b10 runs
var replay = new Replay(e, cat.Spaces[spaceId], AdapterRegistry.Create(cluster), new ReplayOptions { ChunkRows = 2000, AdvanceEveryNs = Min, LatenessNs = Min, FlushAtEnd = true, Speed = 0, Cluster = cluster });
long first = 0, last = 0, span = 0; var prev = new Dictionary<string, long>(); long prevLate = 0;
for (var pass = 0; pass < passes; pass++)
{
    var offset = pass == 0 ? 0 : pass * ((span + 120_000_000_000) / Min + 1) * Min;
    long pf = 0, pl = 0; long records = 0, skipped = 0;
    foreach (var env in CaptureReader.Read(capture))
    {
        if (pf == 0) pf = env.CapturedNs; pl = env.CapturedNs;
        if (pass > 0 && (env.Source == "activity-log" || env.Source.StartsWith("eventhub:", StringComparison.Ordinal))) { skipped++; continue; }
        replay.Process(env with { CapturedNs = env.CapturedNs + offset, Seq = env.Seq + pass * 1_000_000_000L }, false); records++;
    }
    replay.FlushPending();
    if (pass == 0) { first = pf; last = pl; span = last - first; }
    var rows = replay.Stats.Rows; var late = e.Graphs.Sum(g => g.Cx.LateDropped); var lateRej = e.Graphs.Sum(g => g.Cx.LateRejected);
    Console.WriteLine($"pass {pass + 1}: records fed {records:N0} (skipped as absolute-time {skipped:N0}); rows this pass: {string.Join(", ", rows.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value - prev.GetValueOrDefault(x.Key):N0}"))}");
    Console.WriteLine($"        late rows dropped this pass {late - prevLate:N0} (total {late:N0}, rejected {lateRej:N0}); master {e.Master.State.EntityCount:N0} entities, {e.Master.State.RelCount:N0} relationships; logical span {(pl + offset - first) / 1e9 / 3600:F2} h; flow telemetry windows in master {((InMemoryTelemetryStore)e.Telemetry).Dump(e.Master.Key).Select(x => x.Ws).Distinct().Count():N0} distinct window starts");
    prev = new Dictionary<string, long>(rows); prevLate = late;
}

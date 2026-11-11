// X10: journal of adapted rows. Records the row batches and controls that a replay of the newest capture feeds to the engine,
// writes them as dictionary-coded segments, decodes the segments into a fresh engine and compares the state digest and the time.
// usage: dotnet run -c Release -- CAPTURE_DIR DEFINITIONS_DIR OVERLAY_FILE [OUT_DIR]
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

var capture = args[0]; var defs = args[1]; var overlay = args.Length > 2 && args[2] != "-" ? args[2] : null; var outDir = args.Length > 3 ? args[3] : null;
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
Engine NewEngine() => new(cat, spaceId, new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
ReplayOptions Ropts() => new() { ChunkRows = 2000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, FlushAtEnd = true, Speed = 0, Cluster = cluster };

// ---- reference replay with a recorder in front of the engine
var events = new List<Ev>();
string refDigest; double refSecs; Interner refIt;
{
    using var e = NewEngine(); var rec = new Recorder(e, events); refIt = e.Interner;
    var replay = new Replay(rec, cat.Spaces[spaceId], AdapterRegistry.Create(cluster), Ropts());
    var sw = Stopwatch.StartNew(); replay.Run(capture); refSecs = sw.Elapsed.TotalSeconds; refDigest = e.StateDigest();
}
var batches = events.Count(x => x.Kind == 'r'); var rows = events.Where(x => x.Kind == 'r').Sum(x => (long)x.Batch!.Rows);
Console.WriteLine($"recorded {events.Count:N0} events: {batches:N0} row batches with {rows:N0} rows, {events.Count(x => x.Kind == 'a'):N0} advances, {events.Count(x => x.Kind == 'l')} losses; reference replay {refSecs:F1} s, digest {refDigest}");

// ---- envelope size per source directory
Console.WriteLine("envelope size (capture files): source directory | bytes | records");
foreach (var d in Directory.GetDirectories(capture).OrderBy(x => x))
{
    var fs = Directory.EnumerateFiles(d, "*.ndjson", SearchOption.AllDirectories).ToList();
    Console.WriteLine($"{Path.GetFileName(d)} | {fs.Sum(f => new FileInfo(f).Length):N0} | {fs.Sum(f => File.ReadLines(f).LongCount()):N0}");
}

// ---- encode
var enc = new Encoder { It = refIt };
var segments = new List<byte[]>(); var perSource = new Dictionary<string, (long Rows, long Bytes)>();
{
    var seg = new MemoryStream(); var bw = new Writer(seg); var dict = new Dictionary<string, int>();
    foreach (var ev in events)
    {
        var before = seg.Length;
        enc.Write(bw, dict, ev);
        if (ev.Kind == 'r') { var c = perSource.GetValueOrDefault(ev.Source); perSource[ev.Source] = (c.Rows + ev.Batch!.Rows, c.Bytes + (seg.Length - before)); }
        if (seg.Length >= 4 << 20) { segments.Add(seg.ToArray()); seg = new MemoryStream(); bw = new Writer(seg); dict = new Dictionary<string, int>(); }
    }
    if (seg.Length > 0) segments.Add(seg.ToArray());
}
long raw = segments.Sum(s => (long)s.Length);
long Compress(Func<Stream, Stream> make) { long n = 0; foreach (var s in segments) { var ms = new MemoryStream(); using (var z = make(ms)) z.Write(s); n += ms.Length; } return n; }
var swc = Stopwatch.StartNew(); var brotli = Compress(ms => new BrotliStream(ms, CompressionLevel.Optimal, true)); var brotliS = swc.Elapsed.TotalSeconds;
swc.Restart(); var zlib = Compress(ms => new ZLibStream(ms, CompressionLevel.Optimal, true)); var zlibS = swc.Elapsed.TotalSeconds;
swc.Restart(); var brotliFast = Compress(ms => new BrotliStream(ms, CompressionLevel.Fastest, true)); var brotliFastS = swc.Elapsed.TotalSeconds;
Console.WriteLine($"segments: {segments.Count} (>= 4 MB each, dictionary per segment), uncompressed {raw / 1e6:F1} MB = {raw / (double)rows:F1} B per row; Brotli optimal {brotli / 1e6:F1} MB = {brotli / (double)rows:F1} B per row ({brotliS:F1} s); Brotli fastest {brotliFast / 1e6:F1} MB = {brotliFast / (double)rows:F1} B per row ({brotliFastS:F1} s); ZLib optimal {zlib / 1e6:F1} MB = {zlib / (double)rows:F1} B per row ({zlibS:F1} s)");
Console.WriteLine("per row contract (uncompressed bytes of the batches, compressed share by proportion): source | rows | bytes | B per row | Brotli-optimal B per row (proportional)");
foreach (var (s, v) in perSource.OrderByDescending(x => x.Value.Bytes)) Console.WriteLine($"{s} | {v.Rows:N0} | {v.Bytes:N0} | {v.Bytes / (double)v.Rows:F1} | {v.Bytes * (brotli / (double)raw) / v.Rows:F1}");
if (outDir is not null) { Directory.CreateDirectory(outDir); for (var i = 0; i < segments.Count; i++) File.WriteAllBytes(Path.Combine(outDir, $"segment-{i:D4}.bin"), segments[i]); }

// ---- decode and replay from segments (Brotli-compressed segments as stored)
var stored = segments.Select(s => { var ms = new MemoryStream(); using (var z = new BrotliStream(ms, CompressionLevel.Fastest, true)) z.Write(s); return ms.ToArray(); }).ToList();
string segDigest = ""; var segTimes = new List<double>(); var envTimes = new List<double>();
for (var run = 0; run < 5; run++)
{
    using var e = NewEngine(); var dec = new Decoder(e);
    var sw = Stopwatch.StartNew();
    foreach (var blob in stored)
    {
        var ms = new MemoryStream(); using (var z = new BrotliStream(new MemoryStream(blob), CompressionMode.Decompress)) z.CopyTo(ms);
        dec.Run(ms.ToArray());
    }
    segTimes.Add(sw.Elapsed.TotalSeconds); segDigest = e.StateDigest();
}
for (var run = 0; run < 5; run++)
{
    using var e = NewEngine(); var replay = new Replay(e, cat.Spaces[spaceId], AdapterRegistry.Create(cluster), Ropts());
    var sw = Stopwatch.StartNew(); replay.Run(capture); envTimes.Add(sw.Elapsed.TotalSeconds);
}
segTimes.Sort(); envTimes.Sort();
Console.WriteLine($"state digest from segments {segDigest}; from the envelopes {refDigest}; equal: {segDigest == refDigest}");
Console.WriteLine($"replay time (median of 5, load average {Environment.GetEnvironmentVariable("LOADAVG") ?? "see header"}): from segments (decompress, decode, ingest) {segTimes[2]:F2} s [{segTimes[0]:F2} to {segTimes[4]:F2}]; from envelopes (read, parse, adapt, ingest) {envTimes[2]:F2} s [{envTimes[0]:F2} to {envTimes[4]:F2}]; speed-up {envTimes[2] / segTimes[2]:F1}x");

// ================================================================================================
sealed record Ev(char Kind, string Source, Batch? Batch, long Seq, long Value, string Reason);

sealed class Recorder(Engine e, List<Ev> events) : IEngineInput
{
    public Interner Interner => e.Interner;
    public Layout RowLayout(string source) => e.RowLayout(source);
    public void Ingest(string source, Batch rows, long seq) { events.Add(new Ev('r', source, Copy(rows), seq, 0, "")); e.Ingest(source, rows, seq); }
    /// <summary>The engine may mutate a batch it was given (selection, weights): the journal sees a copy taken before the call.</summary>
    static Batch Copy(Batch b)
    {
        if (b.Sel is not null) throw new InvalidOperationException("the adapter produced a selected batch");
        var c = new Batch(b.Layout, b.N) { N = b.N, W = b.W is null ? null : b.W[..b.N] };
        for (var i = 0; i < b.Cols.Length; i++)
        {
            if (b.Layout.IsDec[i]) Array.Copy(b.Cols[i].D, c.Cols[i].D, b.N); else Array.Copy(b.Cols[i].L, c.Cols[i].L, b.N);
            c.Cols[i].P = b.Cols[i].P is null ? null : b.Cols[i].P![..b.N];
        }
        return c;
    }
    public void Advance(long lambdaNs, long seq) { events.Add(new Ev('a', "", null, seq, lambdaNs, "")); e.Advance(lambdaNs, seq); }
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") { events.Add(new Ev('l', "", null, timeNs, count, reason)); e.RecordLoss(timeNs, count, reason); }
}

sealed class Writer(MemoryStream ms)
{
    public void Byte(byte b) => ms.WriteByte(b);
    public void Var(ulong v) { while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; } ms.WriteByte((byte)v); }
    public void Zig(long v) => Var((ulong)((v << 1) ^ (v >> 63)));
    public void Str(string s) { var b = Encoding.UTF8.GetBytes(s); Var((ulong)b.Length); ms.Write(b); }
}

sealed class Reader(byte[] data)
{
    public int Pos;
    public bool Eof => Pos >= data.Length;
    public byte Byte() => data[Pos++];
    public ulong Var() { ulong v = 0; var sh = 0; while (true) { var b = data[Pos++]; v |= (ulong)(b & 0x7F) << sh; if (b < 0x80) return v; sh += 7; } }
    public long Zig() { var v = Var(); return (long)(v >> 1) ^ -(long)(v & 1); }
    public string Str() { var n = (int)Var(); var s = Encoding.UTF8.GetString(data, Pos, n); Pos += n; return s; }
}

sealed class Encoder
{
    public Interner? It;
    public void Write(Writer w, Dictionary<string, int> dict, Ev ev)
    {
        switch (ev.Kind)
        {
            case 'a': w.Byte((byte)'a'); w.Var((ulong)ev.Seq); w.Zig(ev.Value); return;
            case 'l': w.Byte((byte)'l'); w.Zig(ev.Seq); w.Var((ulong)ev.Value); w.Str(ev.Reason); return;
        }
        var b = ev.Batch!;
        w.Byte((byte)'r'); w.Str(ev.Source); w.Byte(1 /* adapter version */); w.Var((ulong)ev.Seq); w.Var((ulong)b.N);
        if (b.W is null) w.Byte(0); else { w.Byte(1); for (var i = 0; i < b.N; i++) w.Zig(b.W[i]); }
        var layout = b.Layout;
        for (var c = 0; c < layout.Count; c++)
        {
            var col = b.Cols[c];
            if (col.P is null) w.Byte(0); else { w.Byte(1); for (var i = 0; i < b.N; i += 8) { byte m = 0; for (var k = 0; k < 8 && i + k < b.N; k++) if (col.P[i + k]) m |= (byte)(1 << k); w.Byte(m); } }
            if (layout.IsDec[c])
            {
                // scaled i64 at the smallest scale that is exact for every present value; else the canonical string
                var s = 0; var ok = true;
                for (var i = 0; i < b.N && ok; i++) { if (!col.Present(i)) continue; var sc = Scale(col.D[i]); if (sc > 18 || Math.Abs(col.D[i] * Pow10(sc)) >= 9.2e18m) ok = false; else s = Math.Max(s, sc); }
                if (ok) for (var i = 0; i < b.N; i++) if (col.Present(i) && Math.Abs(col.D[i] * Pow10(s)) >= 9.2e18m) { ok = false; break; }
                if (ok) { w.Byte((byte)(1 + s)); var m = Pow10(s); long prev = 0; for (var i = 0; i < b.N; i++) { var v = col.Present(i) ? (long)(col.D[i] * m) : 0; w.Zig(v - prev); prev = v; } }
                else { w.Byte(0); for (var i = 0; i < b.N; i++) w.Str(col.Present(i) ? col.D[i].ToString(System.Globalization.CultureInfo.InvariantCulture) : ""); }
            }
            else if (layout.Interned![c])
            {
                for (var i = 0; i < b.N; i++)
                {
                    if (!col.Present(i)) { w.Var(0); continue; }
                    var s = It!.Get((int)col.L[i]);
                    if (!dict.TryGetValue(s, out var id)) { id = dict.Count; dict[s] = id; w.Var((ulong)id + 1); w.Byte(1); w.Str(s); } else { w.Var((ulong)id + 1); w.Byte(0); }
                }
            }
            else { long prev = 0; for (var i = 0; i < b.N; i++) { var v = col.Present(i) ? col.L[i] : 0; w.Zig(v - prev); prev = v; } }
        }
    }
    static int Scale(decimal v) { if (v == 0) return 0; var bits = decimal.GetBits(v); var scale = (bits[3] >> 16) & 0xFF; var lo = (ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32); var hi = (uint)bits[2]; while (scale > 0 && hi == 0 && lo % 10 == 0) { lo /= 10; scale--; } return scale; }
    static decimal Pow10(int s) { decimal r = 1; for (var i = 0; i < s; i++) r *= 10; return r; }
}

sealed class Decoder(Engine e)
{
    public void Run(byte[] data)
    {
        var r = new Reader(data); var dict = new List<string>(); var it = e.Interner;
        while (!r.Eof)
        {
            var kind = (char)r.Byte();
            if (kind == 'a') { var seq = (long)r.Var(); var l = r.Zig(); e.Advance(l, seq); continue; }
            if (kind == 'l') { var t = r.Zig(); var n = (long)r.Var(); var reason = r.Str(); e.RecordLoss(t, n, reason); continue; }
            var source = r.Str(); r.Byte(); var sq = (long)r.Var(); var rows = (int)r.Var();
            var layout = e.RowLayout(source); var b = new Batch(layout, rows) { N = rows };
            if (r.Byte() == 1) { b.W = new long[rows]; for (var i = 0; i < rows; i++) b.W[i] = r.Zig(); }
            for (var c = 0; c < layout.Count; c++)
            {
                var col = b.Cols[c]; bool[]? pres = null;
                if (r.Byte() == 1) { pres = new bool[rows]; for (var i = 0; i < rows; i += 8) { var m = r.Byte(); for (var k = 0; k < 8 && i + k < rows; k++) pres[i + k] = (m & (1 << k)) != 0; } }
                if (layout.IsDec[c])
                {
                    var mode = r.Byte();
                    if (mode == 0) for (var i = 0; i < rows; i++) { var s = r.Str(); if (s.Length > 0) col.D[i] = decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }
                    else { var scale = mode - 1; long prev = 0; for (var i = 0; i < rows; i++) { prev += r.Zig(); col.D[i] = new decimal(prev) / Pow10(scale); } }
                }
                else if (layout.Interned![c])
                {
                    for (var i = 0; i < rows; i++)
                    {
                        var id = (int)r.Var(); if (id == 0) continue;
                        if (r.Byte() == 1) { dict.Add(r.Str()); }
                        col.L[i] = it.Intern(dict[id - 1]);
                    }
                }
                else { long prev = 0; for (var i = 0; i < rows; i++) { prev += r.Zig(); col.L[i] = prev; } }
                col.P = pres;
            }
            e.Ingest(source, b, sq);
        }
    }
    static decimal Pow10(int s) { decimal r = 1; for (var i = 0; i < s; i++) r *= 10; return r; }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>A protocol-level failure with a typed code and an HTTP status (PROTOCOL.md).</summary>
public sealed class HostError(string code, string message, int status = 422, JsonObject? extra = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public JsonObject Extra { get; } = extra ?? [];
    public JsonNode ToJson()
    {
        var e = new JsonObject { ["code"] = Code, ["message"] = Message };
        foreach (var kv in Extra) e[kv.Key] = kv.Value?.DeepClone();
        return new JsonObject { ["error"] = e };
    }
}

public sealed record HostOptions
{
    public string Tenant { get; init; } = "lab";
    public string Cluster { get; init; } = "lab";
    /// <summary>Closed windows a Group keeps revisable behind the frontier (the engine's late horizon). 3 for a live source, which must bound its state; a recorded input is finite and is served with the value `replay` uses (1,000,000), so serving a capture gives the replay result. It is part of what a run computes, so it is stored in the journal header and in every seal.</summary>
    public int LateHorizonWindows { get; init; } = 3;
    public string? SpaceName { get; init; }
    public long InputRowCap { get; init; } = 40_000_000;
    public Func<string, IGraphStore> StoreFactory { get; init; } = _ => new InMemoryGraphStore();
    public string Mode { get; set; } = "replay";
    /// <summary>A durable store (SQLite or Cosmos) that also keeps the journal: the space survives a restart and is rebuilt from it alone.</summary>
    public IGraphStore? Durable { get; init; }
    /// <summary>The durable store already has a journal: replay it into this host before serving.</summary>
    public bool Resume { get; init; }
    /// <summary>Journal entries between two seals (a seal is a snapshot of the whole state; a restart loads the latest one and replays only the entries after it). 0 takes none.</summary>
    public long SealEvery { get; init; }
    public int StartGeneration { get; init; }
    /// <summary>Use the latest seal when there is one (default). Off: the journal is replayed from its first entry (to measure what a seal saves).</summary>
    public bool UseSeal { get; init; } = true;
    /// <summary>Replay the whole journal from its first entry and check everything that can be checked: every recomputed commit against the stored log, and the state at the position of the latest seal against that seal's digest. A seal is not used (it is only checked). Slower, and it reads the entries a seal lets a restart skip.</summary>
    public bool VerifyJournal { get; init; }
    /// <summary>How many patches per graph the host keeps to serve `/patches` and older revisions (0 keeps all). Older revisions answer `Gap` (410) with the first revision still available; the head and every revision number are unaffected.</summary>
    public int RetainPatches { get; init; } = 2_000;
    /// <summary>How many events of the ordered stream the host keeps for subscribers that resume by `seq`. A subscriber that resumes before the oldest gets `gap` and snapshots.</summary>
    public int RetainEvents { get; init; } = 20_000;
}

public sealed class Subscription
{
    public IReadOnlySet<string>? Graphs { get; init; }
    public bool Drafts { get; init; }
    public Channel<string> Messages { get; } = Channel.CreateUnbounded<string>();
    public bool Closed;
}

sealed class Served { public List<Patch> History = []; public long Dropped; public long Offset; public long Head => Dropped + History.Count; public long FirstWindow = long.MinValue; }
sealed record InputRecord(char Kind, string Source, Batch? Rows, long Seq, long Value);

/// <summary>The running space as a service: catalog and engine under one lock, served history per graph, definition edits with re-activation, subscriptions. The HTTP layer is a thin skin over this class.</summary>
public sealed partial class SpaceHost : IEngineInput, IDisposable
{
    public object Gate { get; } = new();
    readonly HostOptions options;
    readonly Interner interner = new();
    readonly Dictionary<string, (string Path, string Text)> sources = new(StringComparer.Ordinal);
    readonly Dictionary<string, Served> served = new(StringComparer.Ordinal);
    readonly List<Patch> catalogHistory = [];
    readonly List<InputRecord> inputs = [];
    readonly List<Subscription> subs = [];
    long inputRows; bool retained = true;
    IJournalStore? journal; bool recovering; int generation; long journalPos, lastSealPosition, replayPos; bool inputsInJournal; long verifyAt; string? verifyDigest; readonly List<DurableView> views = []; long sealedRevisions, entriesReplayed;
    // live input: the envelopes themselves are journaled (the adapters rebuild their state from them), with the position of the source they came from
    Replay? live; string? liveOptions; bool suppress;
    readonly Dictionary<string, string> sourcePositions = new(StringComparer.Ordinal);
    readonly Queue<string> liveKeyOrder = []; readonly HashSet<string> liveKeys = new(StringComparer.Ordinal);
    const int LiveKeyWindow = 20_000;
    long lastTimeEvent; long lambdaSeen;
    string spaceId = "";
    CanvasExport export = null!, catalogExport = null!;

    /// <summary>Identifies this host instance (a run): resources addressed by a revision are immutable within it only.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N");
    /// <summary>Changes whenever the content of a head resource may have changed in this run.</summary>
    public string Stamp => $"{Instance}:{mutations}:{Seq}:{CatalogRevision}";
    long mutations;
    public Catalog Catalog { get; private set; } = null!;
    public Engine Engine { get; private set; } = null!;
    public TypedSpace Space => Catalog.Spaces[spaceId];
    public IReadOnlyList<ISourceAdapter> Adapters { get; private set; } = [];
    public string Mode { get => options.Mode; set => options.Mode = value; }
    public bool Done { get; set; }
    public long InputRows => inputRows;
    public long CatalogRevision => catalogHistory.Count;
    public Interner Interner => interner;
    public Func<JsonObject>? LiveStatus { get; set; }
    public string? DefinitionsDir { get; set; }
    public bool WriteDefinitions { get; set; }

    /// <summary>A loaded and checked catalog with the source text after pin repair. Hosts never mutate a catalog (a commit builds a new one), so one instance can seed many hosts.</summary>
    public sealed record LoadedDefinitions(Catalog Catalog, IReadOnlyDictionary<string, string> FixedText);

    public static LoadedDefinitions LoadDefinitions(IReadOnlyList<SourceFile> files)
    {
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: true);
        if (res.HasErrors) throw new InvalidOperationException(string.Join("\n", res.Diagnostics));
        return new LoadedDefinitions(cat, Catalog.ApplyPinFixes(files, res.PinFixes));
    }

    public SpaceHost(IReadOnlyList<SourceFile> files, HostOptions options) : this(files, options, LoadDefinitions(files)) { }

    public SpaceHost(IReadOnlyList<SourceFile> files, HostOptions options, LoadedDefinitions loaded)
    {
        this.options = options;
        journal = options.Durable as IJournalStore; recovering = options.Resume; generation = options.StartGeneration;
        var cat = loaded.Catalog; var fixedText = loaded.FixedText;
        foreach (var m in cat.Modules.Values.Where(m => m.Name != Ids.KernelModule)) sources[m.Name] = (m.File, fixedText.TryGetValue(m.File, out var ft) ? ft : files.First(f => f.Path == m.File).Text);
        spaceId = options.SpaceName is { } sn ? cat.Spaces.Keys.First(k => Ids.Name(k) == sn) : cat.Spaces.Keys.First();
        Catalog = cat;
        Adapters = AdapterRegistry.Create(options.Cluster);
        var bad = AdapterRegistry.Verify(cat, cat.Spaces[spaceId], Adapters);
        if (bad.Count > 0) throw new InvalidOperationException(string.Join("\n", bad));
        catalogHistory.AddRange(cat.Log.Commits.Select(c => c.Patch));
        Engine = NewEngine(cat);
        Rebuild();
        HookEngine();
    }

    EngineOptions EngineOpts(int gen) => new() { Tenant = options.Tenant, LateHorizonWindows = options.LateHorizonWindows, StoreFactory = options.Durable is null ? options.StoreFactory : _ => { var v = new DurableView(options.Durable, gen, () => recovering); views.Add(v); return v; } };

    Engine NewEngine(Catalog cat) => new(cat, spaceId, EngineOpts(generation), interner);

    void Journal(string body) { if (journal is not null && !recovering && !suppress) { journal.JournalAppend(body); journalPos++; } }

    /// <summary>Opens a space on a durable store. A store without a journal starts a new one (the initial definitions are its first entry); a store with a journal is replayed, and the definitions come from the journal, not from disk. Replaying recomputes every commit and compares it with the stored one.</summary>
    public static SpaceHost OpenDurable(IGraphStore store, IReadOnlyList<SourceFile>? files, HostOptions options)
    {
        var j = store as IJournalStore ?? throw new ArgumentException("the store does not keep a journal");
        if (j.JournalCount == 0)
        {
            var host = new SpaceHost(files ?? throw new ArgumentException("definitions are needed to start a new journal"), options with { Durable = store, Resume = false });
            j.JournalAppend(new JsonObject
            {
                ["k"] = "h", ["tenant"] = options.Tenant, ["lateHorizon"] = options.LateHorizonWindows, ["cluster"] = options.Cluster, ["space"] = host.Space.Name, ["mode"] = options.Mode, ["generation"] = 0,
                ["files"] = new JsonArray([.. host.sources.Values.Select(f => (JsonNode)new JsonObject { ["p"] = f.Path, ["t"] = f.Text })]),
            }.ToJsonString());
            host.journalPos = 1;
            return host;
        }
        string? refusal = null;
        if (options.UseSeal && !options.VerifyJournal && j.SealLatest() is { } seal)
        {
            var fromSeal = TryRestoreFromSeal(j, store, options, seal, out var rejected);
            if (fromSeal is not null) return fromSeal;
            refusal = $"the seal at journal entry {seal.Position} was not used: {rejected}; replaying the whole journal";
            Console.Error.WriteLine(refusal);
        }
        var entries = j.JournalRead().ToList();
        var h = JsonNode.Parse(entries[0])!;
        var hfiles = h["files"]!.AsArray().Select(f => new SourceFile(f!["p"]!.GetValue<string>(), f["t"]!.GetValue<string>())).ToList();
        var restored = new SpaceHost(hfiles, options with { Durable = store, Resume = true, Tenant = h["tenant"]!.GetValue<string>(), LateHorizonWindows = h["lateHorizon"]?.GetValue<int>() ?? 3, Cluster = h["cluster"]!.GetValue<string>(), SpaceName = h["space"]!.GetValue<string>(), Mode = h["mode"]!.GetValue<string>() });
        restored.journalPos = entries.Count; restored.replayPos = 1; restored.inputsInJournal = false;
        if (options.VerifyJournal && j.SealLatest() is { } check) { restored.verifyAt = check.Position; restored.verifyDigest = JsonNode.Parse(check.Meta)!["digest"]!.GetValue<string>(); }
        restored.Recover(entries.Skip(1)); restored.Restored = true; restored.TailReplayed = entries.Count - 1; restored.SealRefusal = refusal;
        return restored;
    }

    /// <summary>True when this host was restored from a seal; the number of journal entries replayed after it; why a seal was refused, when it was.</summary>
    public bool RestoredFromSeal { get; private set; }
    public long TailReplayed { get; private set; }
    public long SealPosition => lastSealPosition;
    public long SealLoadMilliseconds { get; private set; }
    public long TailMilliseconds { get; private set; }
    public int SealsTaken { get; private set; }
    public long LastSealBytes { get; private set; }
    public long SealMilliseconds { get; private set; }

    // ---- live input ----

    static string OptionsJson(ReplayOptions o) => new JsonObject { ["c"] = o.ChunkRows, ["a"] = o.AdvanceEveryNs, ["l"] = o.LatenessNs, ["f"] = o.FlushAtEnd, ["k"] = o.Cluster }.ToJsonString();
    static ReplayOptions ParseReplayOptions(string json) { var n = JsonNode.Parse(json)!; return new ReplayOptions { ChunkRows = n["c"]!.GetValue<int>(), AdvanceEveryNs = n["a"]!.GetValue<long>(), LatenessNs = n["l"]!.GetValue<long>(), FlushAtEnd = n["f"]!.GetValue<bool>(), Cluster = n["k"]!.GetValue<string>() }; }

    void StartLiveCore(ReplayOptions o, string json) { live = new Replay(this, Space, Adapters, o, Gate); liveOptions = json; }

    /// <summary>Starts the live input of this space: envelopes handed to `LiveEnvelope` go through the adapters of the host. A host restored from a journal that has a live input has it already.</summary>
    public void StartLive(ReplayOptions o)
    {
        lock (Gate)
        {
            if (live is not null) return;
            var json = OptionsJson(o); Journal(new JsonObject { ["k"] = "L", ["o"] = json }.ToJsonString());
            StartLiveCore(o, json);
        }
    }

    public bool LiveStarted => live is not null;

    /// <summary>Test hook: called after each step of one live envelope ("journaled", "recorded", "processed"); an exception thrown from it is a crash at that point.</summary>
    public Action<string>? LiveStep { get; set; }

    /// <summary>The texts of the live envelopes in the journal, in order (for rebuilding a recording that lost its last lines to a kill).</summary>
    public IReadOnlyList<string> LiveEnvelopeTexts()
    {
        lock (Gate)
        {
            var res = new List<string>();
            if (journal is null) return res;
            foreach (var body in journal.JournalRead(0))
            {
                if (!body.StartsWith("{\"k\":\"e\"", StringComparison.Ordinal)) continue;
                using var d = System.Text.Json.JsonDocument.Parse(body); res.Add(d.RootElement.GetProperty("t").GetString()!);
            }
            return res;
        }
    }

    /// <summary>The position of a source (an opaque JSON text the input defined) after the last envelope of it that this space has seen, or null.</summary>
    public string? SourcePosition(string source) { lock (Gate) return sourcePositions.GetValueOrDefault(source); }

    public enum LiveResult { Consumed, Duplicate, Rejected }

    static string? KeyOf(string text) { try { using var d = System.Text.Json.JsonDocument.Parse(text); return d.RootElement.TryGetProperty("key", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null; } catch (System.Text.Json.JsonException) { return null; } }

    /// <summary>One envelope of a live source, with the position of the source after it. It is journaled before it is processed, together with its position, so a restart resumes after the last envelope the space has seen, and an envelope delivered twice (at least once delivery) is dropped by its key.</summary>
    public LiveResult LiveEnvelope(string source, string text, string position, Action<string>? record = null)
    {
        lock (Gate)
        {
            if (live is null) throw new InvalidOperationException("the live input has not been started");
            var key = KeyOf(text);
            var env = CaptureReader.Parse(text, "eventhub:" + source, 0);
            if (env is null || (key is not null && liveKeys.Contains(key) && !(Adapters.FirstOrDefault(x => x.Handles(env.Source)) is IRedelivery again && again.Counts(env))))
            {
                Journal(new JsonObject { ["k"] = "p", ["s"] = source, ["p"] = position }.ToJsonString()); sourcePositions[source] = position;
                return env is null ? LiveResult.Rejected : LiveResult.Duplicate;
            }
            Journal(new JsonObject { ["k"] = "e", ["s"] = source, ["t"] = text, ["p"] = position }.ToJsonString());
            LiveStep?.Invoke("journaled");
            record?.Invoke(text);                       // after the journal, before the engine: the recording never has an envelope the journal lacks, and a journaled one it lacks is added at the next start
            LiveStep?.Invoke("recorded");
            LiveApply(source, text, position);
            LiveStep?.Invoke("processed");
            MaybeSeal();
            return LiveResult.Consumed;
        }
    }

    /// <summary>Sends the pending chunk to the engine. The flush is journaled, so a restart cuts the chunks where the live run did and the revisions are the same.</summary>
    public void LiveFlush()
    {
        lock (Gate)
        {
            if (live is null || live.PendingRows == 0) return;
            Journal("{\"k\":\"F\"}");
            suppress = true; try { live.FlushPending(); } finally { suppress = false; }
            MaybeSeal();
        }
    }

    void LiveApply(string source, string text, string position)
    {
        var env = CaptureReader.Parse(text, "eventhub:" + source, 0)!;
        if (KeyOf(text) is { } key && liveKeys.Add(key)) { liveKeyOrder.Enqueue(key); if (liveKeyOrder.Count > LiveKeyWindow) liveKeys.Remove(liveKeyOrder.Dequeue()); }
        sourcePositions[source] = position;
        suppress = true; try { live!.Process(env, pace: false); } finally { suppress = false; }
    }

    // ---- seals ----

    /// <summary>The objects that hold the state of this host and its engine, for a snapshot. Live objects.</summary>
    sealed class HostState
    {
        public Engine.StateRoot Engine = null!;
        public Dictionary<string, Served> Served = null!;
        public List<(long Seq, string? Graph, string Kind, string Text)> Ring = null!;
        public List<Patch> CatalogHistory = null!;
        public Dictionary<string, (string Path, string Text)> Sources = null!;
        public long Seq, LambdaSeen, InputRows; public int Generation; public bool Done;
        public IReadOnlyList<ISourceAdapter> Adapters = null!; public Replay? Live; public Dictionary<string, string> SourcePositions = null!; public Queue<string> LiveKeyOrder = null!; public HashSet<string> LiveKeys = null!;
    }

    HostState StateRootOf() => new() { Engine = Engine.StateRootOf(), Served = served, Ring = ring, CatalogHistory = catalogHistory, Sources = sources, Seq = Seq, LambdaSeen = lambdaSeen, InputRows = inputRows, Generation = generation, Done = Done, Adapters = Adapters, Live = live, SourcePositions = sourcePositions, LiveKeyOrder = liveKeyOrder, LiveKeys = liveKeys };

    static string BuildId => typeof(SpaceHost).Assembly.ManifestModule.ModuleVersionId.ToString();

    /// <summary>The format of a seal's content. 2: telemetry windows of `mean` and `last` carry their pair, and sinks keep the support of windows written under more than one own id (Law 7.5 item 3). A seal of another format is refused with both versions named, and the journal is replayed.</summary>
    public const int SealFormat = 2;
    /// <summary>Why the latest seal was not used at the last start, if it was not.</summary>
    public string? SealRefusal { get; private set; }

    void MaybeSeal() { if (options.SealEvery > 0 && journal is not null && !recovering && journalPos - lastSealPosition >= options.SealEvery) Seal(); }

    /// <summary>Takes a seal now: the state after the journal entries so far, stored with the journal. Called by the host every `SealEvery` entries.</summary>
    public void Seal()
    {
        lock (Gate)
        {
            if (journal is null || recovering) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = StateSnapshot.Save(StateRootOf());
            var meta = new JsonObject
            {
                ["position"] = journalPos, ["format"] = SealFormat, ["build"] = BuildId, ["kernel"] = Kernel.CompiledDigest, ["digest"] = Engine.StateDigest(), ["catalogDigest"] = Catalog.Graph.Digest(), ["generation"] = generation,
                ["tenant"] = options.Tenant, ["lateHorizon"] = options.LateHorizonWindows, ["cluster"] = options.Cluster, ["space"] = Space.Name, ["mode"] = Mode, ["seq"] = Seq,
                ["live"] = liveOptions,
                ["sources"] = new JsonArray([.. sources.Values.Select(f => (JsonNode)new JsonObject { ["p"] = f.Path, ["t"] = f.Text })]),
                ["heads"] = new JsonObject(Engine.Graphs.Select(g => KeyValuePair.Create(g.Name, (JsonNode?)g.Head))),
            }.ToJsonString();
            journal.SealPut(journalPos, meta, data);
            lastSealPosition = journalPos; SealsTaken++; LastSealBytes = data.Length; SealMilliseconds += sw.ElapsedMilliseconds;
        }
    }

    /// <summary>Loads the latest seal into a host built from the definitions the seal names, checks the state digest and the catalog digest against the seal, and replays the journal after it. Null (and a reason) when the seal cannot be used: another build wrote it, the definitions no longer give the same catalog, a digest differs. The journal is the truth; a refused seal costs time, not correctness.</summary>
    static SpaceHost? TryRestoreFromSeal(IJournalStore j, IGraphStore store, HostOptions options, SealRecord seal, out string? rejected)
    {
        rejected = null; SpaceHost? host = null;
        try
        {
            var m = JsonNode.Parse(seal.Meta)!;
            var format = m["format"]?.GetValue<int>() ?? 1;
            if (format != SealFormat) { rejected = $"seal format {format}, this build reads seal format {SealFormat}"; return null; }
            if (m["build"]!.GetValue<string>() != BuildId || m["kernel"]!.GetValue<string>() != Kernel.CompiledDigest) { rejected = "written by another build"; return null; }
            var files = m["sources"]!.AsArray().Select(f => new SourceFile(f!["p"]!.GetValue<string>(), f["t"]!.GetValue<string>())).ToList();
            host = new SpaceHost(files, options with { Durable = store, Resume = true, Tenant = m["tenant"]!.GetValue<string>(), LateHorizonWindows = m["lateHorizon"]?.GetValue<int>() ?? 3, Cluster = m["cluster"]!.GetValue<string>(), SpaceName = m["space"]!.GetValue<string>(), Mode = m["mode"]!.GetValue<string>(), StartGeneration = m["generation"]!.GetValue<int>() });
            if (host.Catalog.Graph.Digest() != m["catalogDigest"]!.GetValue<string>()) { rejected = "the definitions give another catalog than the one sealed"; host.Dispose(); return null; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (m["live"]?.GetValue<string>() is { } liveJson) host.StartLiveCore(ParseReplayOptions(liveJson), liveJson);
            var root = host.StateRootOf();
            StateSnapshot.Load(seal.Data, root);
            host.SealLoadMilliseconds = sw.ElapsedMilliseconds;
            host.Engine.AdoptState(root.Engine);
            host.Seq = root.Seq; host.lambdaSeen = root.LambdaSeen; host.inputRows = root.InputRows; host.Done = root.Done; host.generation = root.Generation;
            if (host.Engine.StateDigest() != m["digest"]!.GetValue<string>()) { rejected = "the restored state digest differs from the sealed one"; host.Dispose(); return null; }
            host.sealedRevisions = host.Engine.Graphs.Sum(g => g.Head);
            host.retained = false; host.inputsInJournal = true; host.journalPos = seal.Position; host.lastSealPosition = seal.Position; host.replayPos = seal.Position;
            var tail = j.JournalRead(seal.Position).ToList();
            host.journalPos = seal.Position + tail.Count;
            sw.Restart(); host.Recover(tail); host.TailMilliseconds = sw.ElapsedMilliseconds; host.Restored = true; host.RestoredFromSeal = true; host.TailReplayed = tail.Count;
            return host;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or InvalidCastException or ArgumentException or IOException or TypeLoadException or NullReferenceException)
        {
            rejected = e.GetType().Name + ": " + e.Message; host?.Dispose(); return null;
        }
    }

    /// <summary>After a restore from a seal the retained input rows (needed to re-activate a changed definition) are not in memory: read them back from the journal, up to the entry being replayed.</summary>
    void LoadInputsFromJournal()
    {
        if (journal is null) throw new HostError("InputsNotRetained", "the retained inputs are not available", 422);
        inputs.Clear(); retained = true;
        var upTo = recovering ? replayPos : journalPos; var n = 0L;
        foreach (var body in journal.JournalRead(0))
        {
            if (++n > upTo) break;
            using var d = System.Text.Json.JsonDocument.Parse(body); var r = d.RootElement;
            switch (r.GetProperty("k").GetString())
            {
                case "r": { var (src, seq, rows) = BatchCodec.Decode(body, RowLayout, interner); inputs.Add(new InputRecord('r', src, rows, seq, rows.Rows)); break; }
                case "a": inputs.Add(new InputRecord('a', "", null, r.GetProperty("q").GetInt64(), r.GetProperty("l").GetInt64())); break;
                case "g": inputs.Add(new InputRecord('g', r.GetProperty("s").GetString()!, null, r.GetProperty("q").GetInt64(), r.GetProperty("u").GetInt64())); break;
                case "l": inputs.Add(new InputRecord('l', r.GetProperty("r").GetString()!, null, r.GetProperty("n").GetInt64(), r.GetProperty("t").GetInt64())); break;
            }
        }
        inputsInJournal = false;
    }

    public long Verified { get; private set; }
    /// <summary>Revisions recomputed from the journal and compared with the stored commit log (those loaded from a seal are not among them).</summary>
    public long Recomputed { get; private set; }
    public long SealedRevisions => sealedRevisions;
    public long EntriesReplayed => entriesReplayed;
    /// <summary>With `VerifyJournal`: the state at the latest seal's position was recomputed and equals the seal's digest.</summary>
    public bool SealChecked { get; private set; }

    /// <summary>What a restart loaded and what it recomputed, in words.</summary>
    public string RestoreSummary() => RestoredFromSeal
        ? $"loaded from the seal at journal entry {lastSealPosition}: state digest and catalog digest checked against the seal; {sealedRevisions:N0} revisions of {Engine.Graphs.Count} graphs came from the seal and were not recomputed; replayed the {entriesReplayed:N0} entries after it and recomputed {Recomputed:N0} revisions, each compared with the stored log; the {lastSealPosition:N0} entries before the seal were not read (--verify-journal replays and checks them)"
        : $"replayed all {entriesReplayed + 1:N0} journal entries and recomputed {Recomputed:N0} revisions of {Engine.Graphs.Count} graphs, each compared with the stored log" + (SealChecked ? $"; the state at the seal (entry {verifyAt}) equals the seal's digest" : "");
    public bool Restored { get; private set; }
    public string Cluster => options.Cluster;

    void Recover(IEnumerable<string> entries)
    {
        try
        {
            var all = entries.ToList();
            for (var i = 0; i < all.Count; i++)
            {
                var body = all[i]; replayPos++; entriesReplayed++;
                using var d = System.Text.Json.JsonDocument.Parse(body); var r = d.RootElement;
                switch (r.GetProperty("k").GetString())
                {
                    case "r": { var (src, seq, rows) = BatchCodec.Decode(body, RowLayout, interner); Ingest(src, rows, seq); break; }
                    case "a": Advance(r.GetProperty("l").GetInt64(), r.GetProperty("q").GetInt64()); break;
                    case "g": Progress(r.GetProperty("s").GetString()!, r.GetProperty("u").GetInt64(), r.GetProperty("q").GetInt64()); break;
                    case "l": RecordLoss(r.GetProperty("t").GetInt64(), r.GetProperty("n").GetInt64(), r.GetProperty("r").GetString()!); break;
                    case "f": Finish(); break;
                    case "L": StartLiveCore(ParseReplayOptions(r.GetProperty("o").GetString()!), r.GetProperty("o").GetString()!); break;
                    case "e": LiveApply(r.GetProperty("s").GetString()!, r.GetProperty("t").GetString()!, r.GetProperty("p").GetString()!); break;
                    case "p": sourcePositions[r.GetProperty("s").GetString()!] = r.GetProperty("p").GetString()!; break;
                    case "F": suppress = true; try { live!.FlushPending(); } finally { suppress = false; } break;
                    case "s": lock (Gate) { Seq++; } break;
                    case "c":
                        {
                            var texts = r.GetProperty("t").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
                            var failedBefore = i + 1 < all.Count && all[i + 1].StartsWith("{\"k\":\"x\"", StringComparison.Ordinal);   // the attempt was refused in the live run too and consumed its generation
                            try { CommitTexts(texts, r.GetProperty("e").GetInt64()); }
                            catch (HostError e) when (failedBefore) { _ = e; }
                            break;
                        }
                    case "x": break;
                    default: throw new InvalidOperationException("unknown journal entry " + body[..Math.Min(40, body.Length)]);
                }
                if (verifyDigest is not null && replayPos == verifyAt)
                {
                    var now = Engine.StateDigest();
                    if (now != verifyDigest) throw new InvalidOperationException($"the journal up to entry {verifyAt} reproduces state {now}, the seal at that entry says {verifyDigest}");
                    SealChecked = true;
                }
            }
        }
        finally { recovering = false; }
        var view = new DurableView(options.Durable!, generation, () => false);
        foreach (var g in Engine.Graphs)
            if (view.Head(new GraphKey(options.Tenant, g.Name)).Head is var stored && stored != g.Head)
                throw new InvalidOperationException($"the store has revision {stored} of {g.Name}, the journal reproduces {g.Head}");
        Verified = Engine.Graphs.Sum(g => g.Head); Recomputed = views.Sum(v => v.Verified);
    }

    void Rebuild()
    {
        export = new CanvasExport(Engine, Catalog);
        catalogExport = new CanvasExport(null!, Catalog, plain: true);
        foreach (var g in Engine.Graphs) if (!served.ContainsKey(g.Name)) served[g.Name] = new Served();
    }

    void HookEngine() => Engine.Committed += OnCommit;

    // ---- engine input ----
    public Layout RowLayout(string source) => Engine.RowLayout(source);

    public void Ingest(string source, Batch rows, long seq)
    {
        lock (Gate)
        {
            if (journal is not null && !recovering && !suppress) { journal.JournalAppend(BatchCodec.Encode(source, seq, rows, interner)); journalPos++; }
            inputRows += rows.Rows; mutations++;
            if (retained) { inputs.Add(new InputRecord('r', source, rows, seq, rows.Rows)); if (inputRows > options.InputRowCap) { retained = false; inputs.Clear(); } }
            Engine.Ingest(source, rows, seq);
            Tick();
            if (!suppress) MaybeSeal();
        }
    }
    public void Advance(long lambdaNs, long seq)
    {
        lock (Gate)
        {
            Journal($"{{\"k\":\"a\",\"l\":{lambdaNs},\"q\":{seq}}}");
            if (retained) inputs.Add(new InputRecord('a', "", null, seq, lambdaNs));
            Engine.Advance(lambdaNs, seq); mutations++;
            lambdaSeen = Math.Max(lambdaSeen, lambdaNs);
            Tick();
            if (!suppress) MaybeSeal();
        }
    }
    public bool WantsProgress => Engine.WantsProgress;
    public void Progress(string source, long untilNs, long seq)
    {
        lock (Gate)
        {
            Journal(new JsonObject { ["k"] = "g", ["s"] = source, ["u"] = untilNs, ["q"] = seq }.ToJsonString());
            if (retained) inputs.Add(new InputRecord('g', source, null, seq, untilNs));
            Engine.Progress(source, untilNs, seq); mutations++;
        }
    }
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss")
    {
        lock (Gate)
        {
            Journal(new JsonObject { ["k"] = "l", ["t"] = timeNs, ["n"] = count, ["r"] = reason }.ToJsonString());
            if (retained) inputs.Add(new InputRecord('l', reason, null, count, timeNs) { });
            Engine.RecordLoss(timeNs, count, reason); mutations++;
        }
    }
    public void Finish() { lock (Gate) { Journal("{\"k\":\"f\"}"); Done = true; mutations++; Publish(new JsonObject { ["type"] = "end" }, null); } }

    void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        if ((now - lastTimeEvent) * 1.0 / Stopwatch.Frequency < 1) return;
        lastTimeEvent = now;
        Publish(new JsonObject { ["type"] = "time", ["logicalTime"] = lambdaSeen == 0 ? null : Norm.Instant(lambdaSeen), ["rows"] = inputRows.ToString(CultureInfo.InvariantCulture) }, null);
    }

    // ---- commit events ----
    void OnCommit(CommitEvent e)
    {
        var sg = served[e.Graph];
        if (e.Patch is not null)
        {
            var p = new Patch(e.Graph, e.Patch.From + sg.Offset, e.Patch.To + sg.Offset, e.Patch.Edits, e.Patch.Cause);
            sg.History.Add(p); Trim(sg);
            Publish(new JsonObject { ["type"] = "patch", ["graph"] = e.Graph, ["patch"] = export.Patch(p) }, e.Graph, telemetry: false);
        }
        // Def. 6.4: a conflict is reported with the commit it appeared, changed or ended in
        if (e.Conflicts.Count > 0)
            Publish(new JsonObject
            {
                ["type"] = "conflicts", ["graph"] = e.Graph,
                ["conflicts"] = new JsonArray([.. e.Conflicts.Select(c => (JsonNode)new JsonObject
                {
                    ["kind"] = c.Kind, ["element"] = c.Element, ["property"] = c.Property is null ? null : export.PropNameOf(c.Property), ["windowStart"] = c.WindowStart is { } ws ? Norm.Instant(ws) : null,
                    ["values"] = new JsonArray([.. c.Values.Select(v => (JsonNode)v)]),
                })]),
            }, e.Graph);
        if (e.Telemetry.Count > 0)
            foreach (var grp in e.Telemetry.GroupBy(c => (c.Key.WindowStart, c.Key.Prop)))
                Publish(new JsonObject { ["type"] = "telemetry", ["graph"] = e.Graph, ["batch"] = TelemetryBatch(e.Graph, grp.Key.WindowStart, grp.Key.Prop, grp.Where(c => c.New is not null).Select(c => (c.Key.Element, c.New!.Value)), grp.Where(c => c.New is null).Select(c => c.Key.Element)) }, e.Graph, telemetry: true);
    }

    /// <summary>Drops the oldest patches of a graph when the retained ones are 10 percent over the declared retention (in one cut, not one at a time).</summary>
    void Trim(Served sg)
    {
        var cap = options.RetainPatches;
        if (cap <= 0 || sg.History.Count <= cap + cap / 10) return;
        var drop = sg.History.Count - cap;
        sg.History.RemoveRange(0, drop); sg.Dropped += drop;
    }

    /// <summary>One window of one property: the elements with a (new) value, and under `removed` the elements whose window was retracted.</summary>
    JsonNode TelemetryBatch(string graph, long ws, int prop, IEnumerable<(int Element, decimal Value)> items, IEnumerable<int>? removed = null)
    {
        var gone = (removed ?? []).Select(interner.Get).ToList();
        var sg = served[graph];
        if (sg.FirstWindow == long.MinValue || ws < sg.FirstWindow) sg.FirstWindow = ws;
        var list = items.ToList();
        var propId = interner.Get(prop);
        var period = Period(graph, propId);
        var ids = list.Select(i => interner.Get(i.Element)).ToList();
        var batch = new JsonObject
        {
            ["graph"] = graph, ["tick"] = (ws - sg.FirstWindow) / Math.Max(period, 1) + 1, ["t"] = (ws - sg.FirstWindow) / 1e9, ["windowStart"] = Norm.Instant(ws),
            ["target"] = ids.Concat(gone).FirstOrDefault()?.StartsWith("r:", StringComparison.Ordinal) == true ? "edge" : "node", ["property"] = export.PropNameOf(propId),
            ["ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]), ["values"] = new JsonArray([.. list.Select(i => (JsonNode)(double)i.Value)]),
        };
        if (gone.Count > 0) batch["removed"] = new JsonArray([.. gone.Select(i => (JsonNode)i)]);
        return batch;
    }

    long Period(string graph, string propId) => Catalog.OntologyOf(Engine.Graph(graph).Typed.OutputModule).Props.TryGetValue(propId, out var p) ? p.Period : 60_000_000_000;

    // ---- subscriptions: one ordered event stream (PROTOCOL.md v3) ----
    readonly List<(long Seq, string? Graph, string Kind, string Text)> ring = [];
    public long Seq { get; private set; }

    /// <summary>Appends an event to the ordered log and delivers it to matching subscriptions. Events of type time are delivered but not logged.</summary>
    void Publish(JsonNode msg, string? graph, bool telemetry = false)
    {
        var kind = msg["type"]!.GetValue<string>();
        if (kind == "draft") Journal("{\"k\":\"s\"}");   // drafts are not stored; the sequence number they consumed is
        if (kind != "time") { msg["seq"] = (++Seq).ToString(CultureInfo.InvariantCulture); }
        var text = msg.ToJsonString();
        if (kind != "time") { ring.Add((Seq, graph, kind, text)); if (options.RetainEvents > 0 && ring.Count > options.RetainEvents + options.RetainEvents / 10) ring.RemoveRange(0, ring.Count - options.RetainEvents); }
        foreach (var s in subs.ToList()) if (!s.Closed && Matches(s, graph, kind)) Enqueue(s, text);
    }

    static bool Matches(Subscription s, string? graph, string kind) =>
        kind is "time" or "end" || (kind == "draft" ? s.Drafts : graph is null ? kind is "activation" : s.Graphs is null || s.Graphs.Contains(graph));

    void Enqueue(Subscription s, string text)
    {
        if (s.Messages.Reader.Count >= 10_000)       // messages written and not yet read
        {
            s.Closed = true; s.Messages.Writer.TryWrite(new JsonObject { ["type"] = "error", ["code"] = "SlowConsumer", ["message"] = "queue exceeded 10000 messages" }.ToJsonString()); s.Messages.Writer.TryComplete(); subs.Remove(s); return;
        }
        s.Messages.Writer.TryWrite(text);
    }
    public void Unsubscribe(Subscription s) { lock (Gate) { s.Closed = true; subs.Remove(s); s.Messages.Writer.TryComplete(); } }

    /// <summary>Snapshots (or the missed events after a sequence number), then live, in order and without gaps: registration and history read happen under one lock.</summary>
    public Subscription Subscribe(IReadOnlyCollection<string>? graphs, bool drafts, long? after)
    {
        lock (Gate)
        {
            var all = new[] { "catalog" }.Concat(Engine.Graphs.Select(g => g.Name)).ToList();
            foreach (var g in graphs ?? []) if (!all.Contains(g)) throw new HostError("UnknownGraph", $"no graph {g}", 404);
            var s = new Subscription { Graphs = graphs is null || graphs.Count == 0 ? null : new HashSet<string>(graphs), Drafts = drafts };
            var wanted = s.Graphs is null ? all : all.Where(s.Graphs.Contains).ToList();
            if (after is > 0 && after > Seq) { Enqueue(s, new JsonObject { ["type"] = "error", ["code"] = "AheadOfHead" }.ToJsonString()); return s; }
            var covered = after is not null && after >= 0 && (ring.Count == 0 ? after >= Seq : ring[0].Seq <= after + 1);
            if (!covered)
            {
                if (after is >= 0) Enqueue(s, new JsonObject { ["type"] = "gap", ["firstSeq"] = (ring.Count == 0 ? Seq + 1 : ring[0].Seq).ToString(CultureInfo.InvariantCulture) }.ToJsonString());
                foreach (var g in wanted) Enqueue(s, new JsonObject { ["type"] = "snapshot", ["graph"] = g, ["seq"] = Seq.ToString(CultureInfo.InvariantCulture), ["snapshot"] = Snapshot(g, null) }.ToJsonString());
            }
            else foreach (var e in ring.Where(e => e.Seq > after && Matches(s, e.Graph, e.Kind))) Enqueue(s, e.Text);
            subs.Add(s);
            return s;
        }
    }

    // ---- reads ----
    public long Head(string g) { lock (Gate) return HeadOf(g); }
    long HeadOf(string g) => g == "catalog" ? catalogHistory.Count : served.TryGetValue(g, out var s) ? s.Head : throw new HostError("UnknownGraph", $"no graph {g}", 404);
    IReadOnlyList<Patch> History(string g) => g == "catalog" ? catalogHistory : served.TryGetValue(g, out var s) ? s.History : throw new HostError("UnknownGraph", $"no graph {g}", 404);
    JsonNode PatchJson(string g, Patch p) => g == "catalog" ? catalogExport.Patch(p) : export.Patch(p);

    static string Rev(long r) => r.ToString(CultureInfo.InvariantCulture);

    public JsonNode SpaceInfo()
    {
        lock (Gate)
        {
            var graphs = new JsonArray
            {
                new JsonObject { ["id"] = "catalog", ["role"] = "catalog", ["head"] = Rev(catalogHistory.Count), ["ontology"] = Ids.KernelModule, ["inputs"] = new JsonArray(), ["direction"] = "readonly", ["editable"] = false, ["descriptor"] = catalogExport.CatalogDescriptor(catalogHistory.Count) },
            };
            foreach (var g in Engine.Graphs)
            {
                var rules = g.Typed.Rules;
                var dir = rules.Count == 0 ? "forward" : rules.All(r => r.Class == "readonly") ? "readonly" : rules.All(r => r.Class == "lens") ? "lens" : "mixed";
                graphs.Add(new JsonObject
                {
                    ["id"] = g.Name, ["role"] = g.IsMaster ? "master" : "projection", ["head"] = Rev(served[g.Name].Head), ["ontology"] = g.Typed.OutputModule,
                    ["inputs"] = new JsonArray([.. g.PortSources.Values.Where(v => Engine.Graphs.Any(x => x.Name == v)).Distinct().Select(v => (JsonNode)v)]),
                    ["direction"] = dir, ["editable"] = rules.Any(r => r.Class is "lens" or "bijective"), ["document"] = g.Typed.OutputModule == Documents.Module, ["description"] = export.DescriptionOf(g), ["dangling"] = Rev(g.State.Rels.Count(r => g.State.Entity(r.Source) is null || g.State.Entity(r.Target) is null)), ["danglingBy"] = DanglingBy(g), ["descriptor"] = export.Descriptor(g, served[g.Name].Head), ["projection"] = g.Typed.Name,
                });
            }
            var input = new JsonObject { ["mode"] = Mode, ["logicalTime"] = lambdaSeen == 0 ? null : Norm.Instant(lambdaSeen), ["rows"] = Rev(inputRows) };
            if (LiveStatus?.Invoke() is { } live) foreach (var kv in live) input[kv.Key] = kv.Value?.DeepClone();
            return new JsonObject { ["space"] = Space.Name, ["protocol"] = "3", ["seq"] = Rev(Seq), ["catalogRevision"] = Rev(catalogHistory.Count), ["kernelDigest"] = Kernel.CompiledDigest, ["graphs"] = graphs, ["input"] = input };
        }
    }

    HostError Gap(string g, long first) => new("Gap", $"history of {g} before revision {first} is not available", 410, new JsonObject { ["graph"] = g, ["lastAvailable"] = Rev(first) });

    Graph StateAt(string g, long revision)
    {
        if (g != "catalog" && served.TryGetValue(g, out var sg) && sg.Dropped > 0)
        {
            if (revision < sg.Dropped) throw Gap(g, sg.Dropped);
            // the oldest patches are gone: walk back from the head state by the inverses of the patches after the revision
            var back = Engine.Graph(g).State.Clone();
            foreach (var p in Enumerable.Reverse(sg.History.Where(p => p.To > revision).ToList())) if (back.Apply(p.Inverse().Edits) is { } err) throw new InvalidOperationException(err);
            return back;
        }
        var gr = new Graph();
        foreach (var p in History(g).Where(p => p.To <= revision)) if (gr.Apply(p.Edits) is { } e) throw new InvalidOperationException(e);
        return gr;
    }

    /// <summary>The document of a document graph at a revision (default: head): Markdown, its digest, the revision and the blocks.</summary>
    public (string Markdown, string Digest, long Revision, JsonNode Json) Document(string g, long? revision)
    {
        lock (Gate)
        {
            var head = HeadOf(g);
            if (g == "catalog" || Engine.Graph(g).Typed.OutputModule != Documents.Module) throw new HostError("NotADocument", $"graph {g} does not have the {Documents.Module} ontology", 404);
            var r = revision ?? head;
            if (r < 0 || r > head) throw new HostError("UnknownRevision", $"head of {g} is {head}", 404);
            var blocks = Documents.BlocksOf(r == head ? Engine.Graph(g).State : StateAt(g, r));
            var md = Documents.Markdown(blocks); var digest = Documents.Digest(md);
            return (md, digest, r, Documents.Json(r, blocks, digest));
        }
    }

    /// <summary>The references of a graph at a revision (default: head), alone and paged by id: `limit` entries after the id `after`. The same entries as `extent.references` of the snapshot.</summary>
    public JsonNode References(string g, long? revision, int limit = 1000, string? after = null)
    {
        lock (Gate)
        {
            var head = HeadOf(g);
            if (g == "catalog") return new JsonObject { ["graph"] = g, ["revision"] = Rev(head), ["references"] = new JsonArray(), ["count"] = "0", ["next"] = null };
            var r = revision ?? head;
            if (r > head || r < 0) throw new HostError("UnknownRevision", $"graph {g} has head {head}", 404);
            var rt = Engine.Graph(g);
            var all = export.Unresolved(r == head ? rt.State : StateAt(g, r), rt);
            var page = all.Where(x => after is null || string.CompareOrdinal(x.Id, after) > 0).Take(Math.Max(limit, 1) + 1).ToList();
            var more = page.Count > Math.Max(limit, 1);
            if (more) page.RemoveAt(page.Count - 1);
            return new JsonObject { ["graph"] = g, ["revision"] = Rev(r), ["references"] = new JsonArray([.. page.Select(x => x.ToJson(export.TypeLabelOrNull))]), ["count"] = Rev(all.Count), ["next"] = more ? page[^1].Id : null };
        }
    }

    public JsonNode Snapshot(string g, long? revision, bool references = true)
    {
        lock (Gate)
        {
            var head = HeadOf(g);
            var r = revision ?? head;
            if (r > head || r < 0) throw new HostError("UnknownRevision", $"graph {g} has head {head}", 404);
            if (g == "catalog") return catalogExport.SnapshotOf(r == head ? Catalog.Graph : StateAt(g, r), catalogExport.CatalogDescriptor(r));
            var rt = Engine.Graph(g);
            return export.SnapshotOf(r == head ? rt.State : StateAt(g, r), export.Descriptor(rt, r), references ? rt : null);
        }
    }

    public JsonNode Descriptor(string g) { lock (Gate) { return g == "catalog" ? catalogExport.CatalogDescriptor(catalogHistory.Count) : export.Descriptor(Engine.Graph(g), served[g].Head); } }
    public JsonNode Ontology(string g) { lock (Gate) { return g == "catalog" ? catalogExport.KernelOntology() : export.Ontology(Engine.Graph(g)); } }

    public IReadOnlyList<JsonNode> Patches(string g, long after, int limit = int.MaxValue)
    {
        lock (Gate)
        {
            HeadOf(g);
            if (g != "catalog" && served.TryGetValue(g, out var sg) && after < sg.Dropped) throw Gap(g, sg.Dropped);
            return [.. History(g).Where(p => p.To > after).Take(limit).Select(p => PatchJson(g, p))];
        }
    }

    public string Definition(string module) { lock (Gate) { return Catalog.Modules.ContainsKey(module) && module != Ids.KernelModule ? Printer.Print(Catalog.Graph, module) : throw new HostError("UnknownModule", $"no module {module}", 404); } }
    public JsonNode Definitions() { lock (Gate) { return new JsonArray([.. Catalog.Modules.Values.Where(m => m.Name != Ids.KernelModule).Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["version"] = m.Version, ["digest"] = m.Digest, ["revision"] = Rev(Catalog.Graph.Entity(m.ReleaseId)?.Props.Text(Ids.KProp("revision")) is { } r ? long.Parse(r, CultureInfo.InvariantCulture) : 0) })]); } }

    // ---- telemetry ----
    /// <summary>The relationships of a graph that name an endpoint it does not hold, by cause: relationship type, the end, the type of the missing entity and whether another graph holds it (`inOtherGraph`, with the graph) or no graph does (`absent`). Empty when every endpoint is in the graph.</summary>
    JsonArray DanglingBy(GraphRuntime g)
    {
        var rows = export.Unresolved(g).SelectMany(u => u.Uses.Select(x => (u, x))).GroupBy(t => (t.x.RelType, t.x.End, t.u.EntityType, t.u.State, t.u.Graph))
            .OrderBy(t => t.Key.RelType, StringComparer.Ordinal).ThenBy(t => t.Key.End, StringComparer.Ordinal).ThenBy(t => t.Key.EntityType, StringComparer.Ordinal);
        return new JsonArray([.. rows.Select(t => (JsonNode)new JsonObject
        {
            ["relationshipType"] = t.Key.RelType, ["end"] = t.Key.End, ["entityType"] = t.Key.EntityType is null ? null : export.TypeLabel(t.Key.EntityType), ["state"] = t.Key.State, ["graph"] = t.Key.Graph,
            ["relationships"] = Rev(t.Sum(x => x.x.N)), ["endpoints"] = Rev(t.Count()),
        })]);
    }

    public JsonNode Telemetry(string g, long? from, long? to, string? property, string? element, bool batches)
    {
        lock (Gate)
        {
            HeadOf(g);
            var rt = Engine.Graph(g);
            var view = Catalog.OntologyOf(rt.Typed.OutputModule);
            var mem = (InMemoryTelemetryStore)Engine.Telemetry;
            var dump = mem.Dump(rt.Key).Where(x => (from is null || x.Ws >= from) && (to is null || x.Ws < to)).ToList();
            var partial = mem.PartialWindows(rt.Key);
            if (property is not null) dump = [.. dump.Where(x => interner.Get(x.Prop) == property || export.PropNameOf(interner.Get(x.Prop)) == property)];
            if (element is not null) dump = [.. dump.Where(x => interner.Get(x.Element) == element)];
            if (batches)
            {
                var windows = dump.Select(x => x.Ws).Distinct().Order().ToList();
                return new JsonArray([.. dump.GroupBy(x => (x.Ws, x.Prop)).OrderBy(x => x.Key.Ws).ThenBy(x => interner.Get(x.Key.Prop), StringComparer.Ordinal).Select(x => TelemetryBatch(g, x.Key.Ws, x.Key.Prop, x.Select(y => (y.Element, y.Value))))]);
            }
            var series = dump.GroupBy(x => (x.Element, x.Prop)).OrderBy(x => interner.Get(x.Key.Element), StringComparer.Ordinal).ThenBy(x => interner.Get(x.Key.Prop), StringComparer.Ordinal).Select(x =>
            {
                var pid = interner.Get(x.Key.Prop); var period = view.Props.TryGetValue(pid, out var pi) ? pi.Period : 60_000_000_000;
                var pts = x.OrderBy(y => y.Ws).ToList();
                var part = pts.Select(y => partial.GetValueOrDefault(y.Ws)).FirstOrDefault(y => y.Reason is not null);
                return (JsonNode)new JsonObject
                {
                    ["element"] = interner.Get(x.Key.Element), ["target"] = interner.Get(x.Key.Element).StartsWith("r:", StringComparison.Ordinal) ? "edge" : "node", ["property"] = export.PropNameOf(pid),
                    ["periodSeconds"] = period / 1_000_000_000, ["completeness"] = part.Reason is null ? "Complete" : $"Partial({part.Reason})",
                    ["points"] = new JsonArray([.. pts.Select(y => (JsonNode)new JsonObject { ["windowStart"] = Norm.Instant(y.Ws), ["value"] = (double)y.Value, ["valueText"] = Norm.Decimal(y.Value) })]),
                };
            });
            return new JsonObject
            {
                ["graph"] = g, ["from"] = from is null ? null : Norm.Instant(from.Value), ["to"] = to is null ? null : Norm.Instant(to.Value), ["series"] = new JsonArray([.. series]),
                // Def. 7.13: windows that ended at or before `retainedFrom` are no longer kept; a range that lies wholly before it is not an empty series but one that is not retained
                ["retainedFrom"] = mem.RetainedFrom(rt.Key) is { } kept ? Norm.Instant(kept) : null, ["notRetained"] = mem.RetainedFrom(rt.Key) is { } edge && to is { } end && end <= edge,
                ["partialWindows"] = new JsonArray([.. partial.Where(kv => (from is null || kv.Key >= from) && (to is null || kv.Key < to)).OrderBy(kv => kv.Key).Select(kv => (JsonNode)new JsonObject { ["windowStart"] = Norm.Instant(kv.Key), ["reason"] = kv.Value.Reason, ["count"] = Rev(kv.Value.Count) })]),
            };
        }
    }

    public JsonNode Meters(string g)
    {
        lock (Gate)
        {
            if (g == "catalog") return new JsonObject { ["meters"] = new JsonArray(), ["thresholds"] = new JsonArray(), ["defaults"] = new JsonObject() };
            HeadOf(g);
            return export.MetersDocument(Engine.Graph(g));
        }
    }

    // ---- lineage ----
    (Engine Engine, LineageReader Reader)? lineageReader;
    LineageReader Lineages { get { var e = Engine; if (lineageReader is not { } c || !ReferenceEquals(c.Engine, e)) lineageReader = (e, new LineageReader(e)); return lineageReader!.Value.Reader; } }

    /// <summary>`GET /graphs/{g}/lineage`: where an element comes from, complete for the rows that hold it up, paged by `limit` and `after`.</summary>
    public JsonNode Lineage(string g, string element, int limit = LineageReader.DefaultLimit, string? after = null)
    {
        lock (Gate)
        {
            if (g == "catalog") throw new HostError("NoLineage", "the catalog has no lineage", 404);
            HeadOf(g);
            return Lineages.Forward(g, element, limit, after);
        }
    }

    /// <summary>`GET /graphs/{g}/lineage/reverse`: the elements of the graphs above that an element is part of.</summary>
    public JsonNode LineageReverse(string g, string element, int limit = LineageReader.DefaultLimit, string? after = null)
    {
        lock (Gate)
        {
            if (g == "catalog") throw new HostError("NoLineage", "the catalog has no lineage", 404);
            HeadOf(g);
            return Lineages.Reverse(g, element, limit, after);
        }
    }

    // ---- definitions ----
    (string Path, string Text)[] SourceList(IReadOnlyDictionary<string, string> texts)
    {
        var list = sources.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (module, text) in texts) list[module] = (list.TryGetValue(module, out var old) ? old.Path : module + ".sattle", text);
        return [.. list.Values];
    }

    static readonly System.Text.RegularExpressions.Regex ModuleHeader = new(@"^module\s+(\S+?)\s*(?:@\s*(\S+?)\s*)?;", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>A module whose version changes is imported as `m @ old` by its dependents; the candidate rewrites those import versions (the pins are re-sealed by the loader), so a version bump commits as one change.</summary>
    Dictionary<string, string> WithImportVersions(IReadOnlyDictionary<string, string> texts)
    {
        var res = new Dictionary<string, string>(texts);
        foreach (var (module, text) in texts)
        {
            var m = ModuleHeader.Match(text);
            var version = m.Success && m.Groups[2].Success ? m.Groups[2].Value : "1.0.0";
            if (!sources.TryGetValue(module, out var old) || ModuleHeader.Match(old.Text) is not { Success: true } om || (om.Groups[2].Success ? om.Groups[2].Value : "1.0.0") == version) continue;
            var oldVersion = om.Groups[2].Success ? om.Groups[2].Value : "1.0.0";
            var rx = new System.Text.RegularExpressions.Regex($@"^(import\s+{System.Text.RegularExpressions.Regex.Escape(module)}\s*@\s*){System.Text.RegularExpressions.Regex.Escape(oldVersion)}(?=\s)", System.Text.RegularExpressions.RegexOptions.Multiline);
            foreach (var (dep, src) in sources.ToList())
            {
                if (dep == module || texts.ContainsKey(dep)) continue;
                var t = res.GetValueOrDefault(dep, src.Text);
                var nt = rx.Replace(t, "${1}" + version);
                if (nt != t) res[dep] = nt;
            }
        }
        return res;
    }

    Catalog Candidate(IReadOnlyDictionary<string, string> texts, out LoadResult res, out Dictionary<string, string> fixedTexts)
    {
        var all = WithImportVersions(texts);
        var files = SourceList(all).Select(f => new SourceFile(f.Path, f.Text)).ToList();
        var cat = new Catalog();
        res = cat.Load(files, updatePins: true);
        fixedTexts = Catalog.ApplyPinFixes(files, res.PinFixes);
        foreach (var kv in all.Where(kv => !texts.ContainsKey(kv.Key)))      // dependents whose import version was rewritten
            if (sources.TryGetValue(kv.Key, out var s) && !fixedTexts.ContainsKey(s.Path)) fixedTexts[s.Path] = kv.Value;
        return cat;
    }

    string ModuleOfPath(string? path) => sources.FirstOrDefault(kv => kv.Value.Path == path).Key ?? (path is null ? "" : Path.GetFileNameWithoutExtension(path));

    JsonNode DiagJson(Diagnostic d) => new JsonObject
    {
        ["code"] = d.Code, ["severity"] = d.Severity, ["message"] = d.Message, ["line"] = d.Line, ["column"] = d.Col, ["element"] = d.Focus.Length > 0 && d.Focus.StartsWith("d:", StringComparison.Ordinal) ? d.Focus : null,
        ["module"] = ModuleOfPath(d.File),
    };

    string ParseModuleName(string module, string text)
    {
        try { var ast = Parser.Parse(text); if (ast.Name != module) throw new HostError("ModuleMismatch", $"text declares module {ast.Name}, not {module}", 422); return ast.Name; }
        catch (SyntaxError) { return module; }
    }

    public JsonNode Check(string module, string text) => CheckTexts(new Dictionary<string, string> { [module] = text });

    JsonNode CheckTexts(IReadOnlyDictionary<string, string> texts)
    {
        lock (Gate)
        {
            foreach (var (module, text) in texts)
            {
                if (module == Ids.KernelModule) throw new HostError("KernelFrozen", "the kernel is compiled into the engine", 422);
                ParseModuleName(module, text);
            }
            var cand = Candidate(texts, out var res, out _);
            var diags = new JsonArray([.. res.Diagnostics.Select(DiagJson)]);
            var ok = !res.HasErrors;
            JsonNode? patch = null; var affectedModules = new List<string>(); var affectedGraphs = new List<string>();
            if (ok)
            {
                var head = catalogHistory.Count;
                var edits = Graph.Diff(Catalog.Graph, cand.Graph);
                patch = catalogExport.Patch(new Patch("catalog", head, head + 1, edits));
                affectedModules = [.. cand.Modules.Values.Where(m => !Catalog.Modules.TryGetValue(m.Name, out var o) || o.Digest != m.Digest).Select(m => m.Name).Where(n => n != Ids.KernelModule).Order()];
                foreach (var g in Space.Graphs)
                {
                    var proj = cand.Projections.GetValueOrDefault(g.ProjectionId);
                    if (proj is null) continue;
                    if (ProjectionChecker.Closure(cand.Graph, proj.Module).Intersect(affectedModules).Any()) affectedGraphs.Add(g.Name);
                }
            }
            return new JsonObject
            {
                ["ok"] = ok, ["baseRevision"] = Rev(catalogHistory.Count), ["diagnostics"] = diags, ["patch"] = patch,
                ["affected"] = new JsonObject { ["modules"] = new JsonArray([.. affectedModules.Select(m => (JsonNode)m)]), ["graphs"] = new JsonArray([.. affectedGraphs.Select(m => (JsonNode)m)]) },
                ["schemas"] = Schemas(cand),
            };
        }
    }

    /// <summary>The row schema after each operator of the projections of the given modules, as the check typed them (also when the module has errors: the operators that typed are listed). Per projection id, in plan order: the operator's id, name and kind, its window (or null), and its columns with their kind, element type, unit, whether the value is always present, and its role (data, or telemetry with temporality and period).</summary>
    /// <summary>The row schema after each operator of every projection of a catalog, by projection id.</summary>
    internal static JsonObject Schemas(Catalog cand)
    {
        static JsonNode Column(Col c) => new JsonObject
        {
            ["name"] = c.Name,
            ["kind"] = c.T.Ty.Tag switch { Tag.Ent => "Entity", Tag.Rel => "Relationship", var t => t.ToString() },
            ["type"] = c.T.Ty.TypeId, ["unit"] = c.T.Ty.IsNumeric && c.T.Ty.Unit != "1" ? c.T.Ty.Unit : null, ["item"] = c.T.Ty.Item?.ToString(),
            ["required"] = c.T.Req,
            ["role"] = c.T.Role.IsData ? "data" : "telemetry", ["temporality"] = c.T.Role.Temporality, ["period"] = c.T.Role.IsData ? null : Lexer.DurationText(c.T.Role.Period),
        };
        var res = new JsonObject();
        foreach (var (pid, ops) in cand.OperatorTypes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            res[pid] = new JsonArray([.. ops.Select(op => (JsonNode)new JsonObject
            {
                ["op"] = op.Id, ["name"] = op.Name, ["kind"] = op.Kind, ["window"] = op.Out.Window == 0 ? null : Lexer.DurationText(op.Out.Window),
                ["columns"] = new JsonArray([.. op.Out.Cols.Select(Column)]),
            })]);
        return res;
    }

    /// <summary>A configuration edit: commit the catalog patch, rebuild the plan, replay the retained inputs once in order into fresh state, emit only the final difference per graph (ADR-064 to 071).</summary>
    public JsonNode Commit(string module, string text, long expectedRevision) => CommitTexts(new Dictionary<string, string> { [module] = text }, expectedRevision);

    JsonNode CommitTexts(IReadOnlyDictionary<string, string> texts, long expectedRevision)
    {
        lock (Gate)
        {
            var module = string.Join("+", texts.Keys.Order());
            foreach (var (m, t) in texts)
            {
                if (m == Ids.KernelModule) throw new HostError("KernelFrozen", "the kernel is compiled into the engine", 422);
                ParseModuleName(m, t);
            }
            if (expectedRevision != catalogHistory.Count) throw new HostError("StaleRevision", $"catalog head is {catalogHistory.Count}", 409, new JsonObject { ["actual"] = Rev(catalogHistory.Count) });
            var cand = Candidate(texts, out var res, out var fixedTexts);
            if (res.HasErrors) throw new HostError("Invalid", "the definitions do not check", 422, new JsonObject { ["diagnostics"] = new JsonArray([.. res.Diagnostics.Select(DiagJson)]) });
            if (!cand.Spaces.Keys.Any(k => Ids.Name(k) == Space.Name)) throw new HostError("ActivationRejected", $"graph space {Space.Name} no longer activates", 422);
            var newSpaceId = cand.Spaces.Keys.First(k => Ids.Name(k) == Space.Name);
            var bad = AdapterRegistry.Verify(cand, cand.Spaces[newSpaceId], Adapters);
            if (bad.Count > 0) throw new HostError("ActivationRejected", string.Join("; ", bad.Select(b => b.Message)), 422);
            if (Graph.Diff(Catalog.Graph, cand.Graph).Count == 0)
                return new JsonObject { ["catalogRevision"] = Rev(catalogHistory.Count), ["activation"] = new JsonObject { ["graphs"] = new JsonArray(), ["replayedRows"] = "0", ["milliseconds"] = "0" } }; // no change, no revision
            if (!retained && inputsInJournal) LoadInputsFromJournal();
            if (!retained) throw new HostError("InputsNotRetained", "the retained inputs exceed the cap; re-activation needs a restart", 422);

            var sw = Stopwatch.StartNew(); mutations++;
            Journal(new JsonObject { ["k"] = "c", ["e"] = expectedRevision, ["t"] = new JsonObject(texts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))) }.ToJsonString());
            var oldSpace = spaceId; spaceId = newSpaceId;
            Engine fresh;
            try
            {
                fresh = new Engine(cand, newSpaceId, EngineOpts(++generation), interner);
                foreach (var r in inputs)
                    switch (r.Kind) { case 'r': fresh.Ingest(r.Source, r.Rows!, r.Seq); break; case 'a': fresh.Advance(r.Value, r.Seq); break; case 'g': fresh.Progress(r.Source, r.Value, r.Seq); break; case 'l': fresh.RecordLoss(r.Value, r.Seq, r.Source.Length == 0 ? "sourceLoss" : r.Source); break; }
            }
            catch (Exception e) { spaceId = oldSpace; Journal("{\"k\":\"x\"}"); throw new HostError("ActivationRejected", e.Message, 422); }

            // final difference per graph, numbered after the served head
            var changed = new List<(string Id, long From, long To, int Edits)>();
            var old = Engine; var oldNames = old.Graphs.Select(g => g.Name).ToHashSet();
            old.Committed -= OnCommit;
            var newPatches = new List<(string Graph, Patch Patch)>();
            foreach (var g in fresh.Graphs)
            {
                if (!served.TryGetValue(g.Name, out var sg)) served[g.Name] = sg = new Served();
                var oldState = oldNames.Contains(g.Name) ? old.Graph(g.Name).State : new Graph();
                var edits = Graph.Diff(oldState, g.State);
                if (edits.Count > 0)
                {
                    var p = new Patch(g.Name, sg.Head, sg.Head + 1, edits, new Cause([$"definition:{module}"], (catalogHistory.Count + 1).ToString(CultureInfo.InvariantCulture)));
                    newPatches.Add((g.Name, p)); changed.Add((g.Name, sg.Head, sg.Head + 1, edits.Count));
                }
            }
            // catalog patch
            var catPatch = new Patch("catalog", catalogHistory.Count, catalogHistory.Count + 1, Graph.Diff(Catalog.Graph, cand.Graph));
            // swap
            Engine = fresh; Catalog = cand;
            foreach (var kv in fixedTexts) { var m = ModuleOfPath(kv.Key); if (m.Length > 0) sources[m] = (kv.Key, kv.Value); }
            foreach (var (m, t) in texts) sources[m] = (sources.TryGetValue(m, out var oldSrc) ? oldSrc.Path : m + ".sattle", t);
            if (WriteDefinitions && DefinitionsDir is not null && !recovering) foreach (var e in sources.Values) if (File.Exists(e.Path)) File.WriteAllText(e.Path, e.Text);
            if (catPatch.Edits.Length > 0) catalogHistory.Add(catPatch); else catalogHistory.Add(new Patch("catalog", catalogHistory.Count, catalogHistory.Count + 1, []));
            Rebuild();
            foreach (var (g, p) in newPatches) { served[g].History.Add(p); Trim(served[g]); }
            foreach (var g in fresh.Graphs) served[g.Name].Offset = served[g.Name].Head - g.Head;
            HookEngine();
            Publish(new JsonObject { ["type"] = "patch", ["graph"] = "catalog", ["patch"] = catalogExport.Patch(catalogHistory[^1]) }, "catalog");
            foreach (var (g, p) in newPatches) Publish(new JsonObject { ["type"] = "patch", ["graph"] = g, ["patch"] = export.Patch(p) }, g);
            Publish(new JsonObject { ["type"] = "activation", ["catalogRevision"] = Rev(catalogHistory.Count), ["graphs"] = new JsonArray([.. changed.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["from"] = Rev(c.From), ["to"] = Rev(c.To) })]) }, null);
            old.Dispose();
            return new JsonObject
            {
                ["catalogRevision"] = Rev(catalogHistory.Count),
                ["activation"] = new JsonObject
                {
                    ["graphs"] = new JsonArray([.. changed.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["from"] = Rev(c.From), ["to"] = Rev(c.To), ["edits"] = Rev(c.Edits) })]),
                    ["replayedRows"] = Rev(inputRows), ["milliseconds"] = Rev(sw.ElapsedMilliseconds),
                },
            };
        }
    }

    public JsonNode Editability(string g)
    {
        lock (Gate)
        {
            HeadOf(g);
            if (g == "catalog") return new JsonObject { ["graph"] = g, ["rules"] = new JsonArray() };
            var rt = Engine.Graph(g);
            return new JsonObject
            {
                ["graph"] = g,
                ["rules"] = new JsonArray([.. rt.Typed.Rules.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name, ["class"] = r.Class, ["accept"] = new JsonArray([.. (r.Policy?.Accept ?? []).Select(a => (JsonNode)a)]), ["reason"] = r.Reason,
                    ["owns"] = new JsonArray([.. Owned(rt.Typed, r.Name).Select(a => (JsonNode)a)]),
                })]),
            };
        }
    }

    public string StateDigest() { lock (Gate) return Engine.StateDigest(); }
    public void Dispose() { lock (Gate) Engine.Dispose(); }
}

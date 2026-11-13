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
    /// <summary>Closed windows a Group keeps revisable behind the frontier (the engine's late horizon). 3 for a live source, which must bound its state; a recorded input is finite and is served as `replay` runs it (no late horizon), so serving a capture gives the replay result. Null is no late horizon. It is part of what a run computes, so it is stored in the journal header and in every seal.</summary>
    public int? LateHorizonWindows { get; init; } = 3;

    /// <summary>The late horizon a journal header or a seal recorded: the number, none where it recorded none, 3 where the record is older than the field.</summary>
    internal static int? StoredLateHorizon(JsonNode record) => record.AsObject().TryGetPropertyValue("lateHorizon", out var stored) ? stored?.GetValue<int>() : 3;
    public string? SpaceName { get; init; }
    public long InputRowCap { get; init; } = 40_000_000;
    public Func<string, IGraphStore> StoreFactory { get; init; } = _ => new HeadOnlyGraphStore();
    /// <summary>Journal entries between two seals (a seal is a snapshot of the whole state; a restart loads the latest one and replays only the entries after it). 0 takes none.</summary>
    public long SealEvery { get; init; }
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
/// <summary>What an engine takes, as a value: a batch of rows, an advance of logical time, the progress of a source, a loss. A journal entry of these kinds is one; an envelope of a live source gives several.</summary>
public abstract record EngineInput
{
    public sealed record Rows(string Source, Batch Batch, long Seq) : EngineInput;
    public sealed record Advance(long Lambda, long Seq) : EngineInput;
    public sealed record Progress(string Source, long Until, long Seq) : EngineInput;
    public sealed record Loss(long Time, long Count, string Reason) : EngineInput;

    public long RowCount => this is Rows r ? r.Batch.Rows : 0;

    public void ApplyTo(IEngineInput engine)
    {
        switch (this)
        {
            case Rows r: engine.Ingest(r.Source, r.Batch, r.Seq); break;
            case Advance a: engine.Advance(a.Lambda, a.Seq); break;
            case Progress g: engine.Progress(g.Source, g.Until, g.Seq); break;
            case Loss l: engine.RecordLoss(l.Time, l.Count, l.Reason); break;
        }
    }

    /// <summary>The engine inputs that journal entries stand for, in order. Rows, advances, progress and losses are inputs themselves. The envelopes of a live input are run again through `adapters` (a fresh set) with the options the live input was started with, and cut into batches where the recorded flushes cut them: what the live run did, done again. `layoutOf` and `interner` are those the rows are built with.</summary>
    public static IEnumerable<EngineInput> Derive(IEnumerable<Entry> entries, TypedSpace space, IReadOnlyList<ISourceAdapter> adapters, Func<string, Layout> layoutOf, Interner interner, bool wantsProgress)
    {
        var collected = new Collector(layoutOf, interner, wantsProgress); Replay? live = null;
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case Entry.Rows r: yield return new Rows(r.Source, r.Batch, r.Seq); break;
                case Entry.Advance a: yield return new Advance(a.Lambda, a.Seq); break;
                case Entry.Progress g: yield return new Progress(g.Source, g.Until, g.Seq); break;
                case Entry.Loss l: yield return new Loss(l.Time, l.Count, l.Reason); break;
                case Entry.LiveStart start: live = new Replay(collected, space, adapters, start.Options); break;
                case Entry.Envelope e: live!.Process(CaptureReader.Parse(e.Body, "eventhub:" + e.Source, 0)!, pace: false); break;
                case Entry.Flush: live!.FlushPending(); break;
            }
            foreach (var input in collected.Take()) yield return input;
        }
    }

    /// <summary>An engine input that keeps what it is given.</summary>
    sealed class Collector(Func<string, Layout> layoutOf, Interner interner, bool wantsProgress) : IEngineInput
    {
        List<EngineInput> got = [];
        public List<EngineInput> Take() { var taken = got; got = []; return taken; }
        public Interner Interner => interner;
        public Layout RowLayout(string source) => layoutOf(source);
        public bool WantsProgress => wantsProgress;
        public void Ingest(string source, Batch rows, long seq) => got.Add(new Rows(source, rows, seq));
        public void Advance(long lambdaNs, long seq) => got.Add(new Advance(lambdaNs, seq));
        public void Progress(string source, long untilNs, long seq) => got.Add(new Progress(source, untilNs, seq));
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => got.Add(new Loss(timeNs, count, reason));
    }
}

/// <summary>Where a host is in its life. `Replaying`: it reads its journal back; nothing is written to the journal, no seal is taken, and each commit the engine makes is compared with the stored one. `Running`: what enters is written to the journal first. `Finished`: as `Running`, and the recorded input has ended.</summary>
public enum Phase { Replaying, Running, Finished }

/// <summary>What opening a space did on a store that already had its journal: whether a seal was loaded and at which entry, how many entries were replayed after it, how many revisions came from the seal and how many were recomputed and compared with the stored log, whether the state at the seal's entry was checked (`VerifyJournal`), why a seal was not used, and the times.</summary>
public sealed record Restart(bool FromSeal, long SealPosition, long EntriesReplayed, long SealedRevisions, long Recomputed, long Verified, bool SealChecked, string? SealRefusal, long SealLoadMilliseconds, long TailMilliseconds);

/// <summary>The running space as a service: catalog and engine under one lock, served history per graph, definition edits with re-activation, subscriptions. The HTTP layer is a thin skin over this class.</summary>
public sealed partial class SpaceHost : IEngineInput, IDisposable
{
    public object Gate { get; } = new();
    readonly HostOptions options;
    readonly Interner interner = new();
    readonly Dictionary<string, (string Path, string Text)> sources = new(StringComparer.Ordinal);
    readonly Dictionary<string, Served> served = new(StringComparer.Ordinal);
    readonly List<Patch> catalogHistory = [];
    /// <summary>The engine inputs so far, while they are in memory: not after a restart from a seal (they are in the journal), and not beyond the row cap.</summary>
    List<EngineInput>? kept = [];
    readonly List<Subscription> subs = [];
    long inputRows, fed;
    readonly IGraphStore? durable; readonly IJournalStore? journal; Phase phase = Phase.Running; int generation; long journalPos, lastSealPosition; readonly List<DurableView> views = [];
    // live input: the envelopes themselves are journaled (the adapters rebuild their state from them), with the position of the source they came from
    Replay? live; string? liveOptions;
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
    /// <summary>"live" once a live input was started, else "replay".</summary>
    public string Mode => live is null ? "replay" : "live";
    public Phase Phase => phase;
    /// <summary>What opening this space did, when its store already had a journal; null for a space that was started new.</summary>
    public Restart? Restart { get; private set; }
    /// <summary>The late horizon the space runs with (a restart takes it from the store).</summary>
    public int? LateHorizonWindows => options.LateHorizonWindows;
    /// <summary>The recorded input has ended.</summary>
    public bool Done => phase == Phase.Finished;
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

    public SpaceHost(IReadOnlyList<SourceFile> files, HostOptions options, LoadedDefinitions loaded) : this(files, options, loaded, null, Phase.Running, 0, null) { }

    /// <summary>`texts`: the text table of the seal this host will take its state from. The plans are compiled against it, so a text they name has the number it had when the seal was taken.</summary>
    SpaceHost(IReadOnlyList<SourceFile> files, HostOptions options, LoadedDefinitions loaded, IGraphStore? durable, Phase phase, int generation, IReadOnlyList<string>? texts)
    {
        this.options = options;
        if (texts is not null) interner.ReplaceAll(texts);
        this.durable = durable; journal = durable as IJournalStore; this.phase = phase; this.generation = generation;
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

    EngineOptions EngineOpts(int gen) => new() { Tenant = options.Tenant, LateHorizonWindows = options.LateHorizonWindows, StoreFactory = durable is null ? options.StoreFactory : _ => { var v = new DurableView(durable, gen, () => phase == Phase.Replaying); views.Add(v); return v; } };

    Engine NewEngine(Catalog cat) => new(cat, spaceId, EngineOpts(generation), interner);

    /// <summary>Writes an entry to the journal, unless the journal is being read back.</summary>
    void Append(Entry entry) { if (journal is not null && phase != Phase.Replaying) { journal.JournalAppend(entry.Text(interner)); journalPos++; } }

    /// <summary>The one way an input enters: the entry is written to the journal, then applied. `journaled` runs between the two. A restart applies the entries it reads without writing them.</summary>
    void Accept(Entry entry, Action? journaled = null)
    {
        lock (Gate)
        {
            Append(entry);
            journaled?.Invoke();
            Apply(entry);
            MaybeSeal();
        }
    }

    /// <summary>The phase after an entry.</summary>
    static Phase Next(Phase phase, Entry entry) => entry is Entry.Finish && phase == Phase.Running ? Phase.Finished : phase;

    /// <summary>What an entry does to the space. A definition commit is not applied here: its checks come before its entry is written (`CommitTexts`).</summary>
    void Apply(Entry entry)
    {
        switch (entry)
        {
            case Entry.Rows r: Feed(new EngineInput.Rows(r.Source, r.Batch, r.Seq)); break;
            case Entry.Advance a: Feed(new EngineInput.Advance(a.Lambda, a.Seq)); break;
            case Entry.Progress g: Feed(new EngineInput.Progress(g.Source, g.Until, g.Seq)); break;
            case Entry.Loss l: Feed(new EngineInput.Loss(l.Time, l.Count, l.Reason)); break;
            case Entry.Finish: phase = Next(phase, entry); mutations++; Publish(new JsonObject { ["type"] = "end" }, null); break;
            case Entry.LiveStart start: live = new Replay(new Derived(this), Space, Adapters, start.Options, Gate); liveOptions = Entry.OptionsText(start.Options); break;
            case Entry.Envelope e: LiveApply(e.Source, e.Body, e.After); break;
            case Entry.Skipped p: sourcePositions[p.Source] = p.After; break;
            case Entry.Flush: live!.FlushPending(); break;
            case Entry.SequenceStep: Seq++; break;
            case Entry.CommitRefused: break;
            default: throw new InvalidOperationException("the entry " + entry.GetType().Name + " cannot be applied here");
        }
    }

    /// <summary>An engine input reaches the engine: counted, kept for a later re-activation while the inputs fit, and applied. The rows an envelope gives come here directly; they are no journal entries, the envelope is.</summary>
    void Feed(EngineInput input)
    {
        mutations++; fed++;
        inputRows += input.RowCount;
        if (kept is not null) { kept.Add(input); if (inputRows > options.InputRowCap) kept = null; }
        input.ApplyTo(Engine);
        if (input is EngineInput.Advance a) lambdaSeen = Math.Max(lambdaSeen, a.Lambda);
        if (input is EngineInput.Rows or EngineInput.Advance) Tick();
    }

    /// <summary>The engine inputs so far, in order: what a definition is activated on again. From memory while they are there, else from the journal.</summary>
    IEnumerable<EngineInput> Inputs()
    {
        if (kept is not null) return kept;
        if (journal is null) throw new HostError("InputsNotRetained", "the inputs of this space are beyond the row cap and it has no journal to read them from; activating a changed definition needs a restart", 422);
        var entries = journal.JournalRead(0).Take((int)Math.Min(journalPos, int.MaxValue)).Skip(1).Select(text => Entry.Parse(text, RowLayout, interner));
        return EngineInput.Derive(entries, Space, AdapterRegistry.Create(options.Cluster), RowLayout, interner, WantsProgress);
    }

    /// <summary>A fresh engine for a candidate catalog, given every input so far, or as many as hold `rowLimit` rows. `prepare` runs on the engine before its first input. Used by a definition commit and by a preview.</summary>
    (Engine Engine, long Rows, bool Complete) Reactivate(Catalog candidate, string space, EngineOptions engineOptions, long rowLimit = long.MaxValue, Action<Engine>? prepare = null)
    {
        var fresh = new Engine(candidate, space, engineOptions, interner);
        try
        {
            prepare?.Invoke(fresh);
            long rows = 0;
            foreach (var input in Inputs())
            {
                if (rows >= rowLimit) return (fresh, rows, false);
                input.ApplyTo(fresh); rows += input.RowCount;
            }
            return (fresh, rows, true);
        }
        catch { fresh.Dispose(); throw; }
    }

    /// <summary>What the live input writes its rows to: the engine, through `Feed`.</summary>
    sealed class Derived(SpaceHost host) : IEngineInput
    {
        public Interner Interner => host.interner;
        public Layout RowLayout(string source) => host.RowLayout(source);
        public bool WantsProgress => host.WantsProgress;
        public void Ingest(string source, Batch rows, long seq) => host.Feed(new EngineInput.Rows(source, rows, seq));
        public void Advance(long lambdaNs, long seq) => host.Feed(new EngineInput.Advance(lambdaNs, seq));
        public void Progress(string source, long untilNs, long seq) => host.Feed(new EngineInput.Progress(source, untilNs, seq));
        public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => host.Feed(new EngineInput.Loss(timeNs, count, reason));
    }

    /// <summary>Opens a space on a durable store. A store without a journal starts a new one (the initial definitions are its first entry); a store with a journal is replayed, and the definitions come from the journal, not from disk. Replaying recomputes every commit and compares it with the stored one.</summary>
    public static SpaceHost OpenDurable(IGraphStore store, IReadOnlyList<SourceFile>? files, HostOptions options)
    {
        var j = store as IJournalStore ?? throw new ArgumentException("the store does not keep a journal");
        if (j.JournalCount == 0)
        {
            var given = files ?? throw new ArgumentException("definitions are needed to start a new journal");
            var host = new SpaceHost(given, options, LoadDefinitions(given), store, Phase.Running, 0, null);
            j.JournalAppend(new Entry.Header(options.Tenant, options.LateHorizonWindows, options.Cluster, host.Space.Name, host.Mode, [.. host.sources.Values.Select(f => new SourceFile(f.Path, f.Text))]).Text(host.interner));
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
        var h = (Entry.Header)Entry.Parse(entries[0], _ => throw new InvalidDataException("the journal does not start with its header"), new Interner());
        var restored = new SpaceHost(h.Files, options with { Tenant = h.Tenant, LateHorizonWindows = h.LateHorizon, Cluster = h.Cluster, SpaceName = h.Space }, LoadDefinitions(h.Files), store, Phase.Replaying, 0, null);
        restored.journalPos = 1;
        var check = options.VerifyJournal ? j.SealLatest() : null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (recomputed, verified, sealChecked) = restored.Recover(entries.Skip(1), Phase.Running, check?.Position ?? 0, check is null ? null : JsonNode.Parse(check.Meta)!["digest"]!.GetValue<string>());
        restored.Restart = new Restart(false, check?.Position ?? 0, entries.Count - 1, 0, recomputed, verified, sealChecked, refusal, 0, sw.ElapsedMilliseconds);
        return restored;
    }

    /// <summary>The journal entry of the latest seal, taken or loaded.</summary>
    public long SealPosition => lastSealPosition;
    public int SealsTaken { get; private set; }
    public long LastSealBytes { get; private set; }
    public long SealMilliseconds { get; private set; }

    // ---- live input ----

    /// <summary>Starts the live input of this space: envelopes handed to `LiveEnvelope` go through the adapters of the host. A host restored from a journal that has a live input has it already.</summary>
    public void StartLive(ReplayOptions o) { lock (Gate) { if (live is null) Accept(new Entry.LiveStart(o)); } }

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
                if (Entry.KindOf(body) == 'e') res.Add(((Entry.Envelope)Entry.Parse(body, RowLayout, interner)).Body);
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
                Accept(new Entry.Skipped(source, position));
                return env is null ? LiveResult.Rejected : LiveResult.Duplicate;
            }
            // the recording is written after the journal and before the engine: it never has an envelope the journal lacks, and a journaled one it lacks is added at the next start
            Accept(new Entry.Envelope(source, text, position), () => { LiveStep?.Invoke("journaled"); record?.Invoke(text); LiveStep?.Invoke("recorded"); });
            LiveStep?.Invoke("processed");
            return LiveResult.Consumed;
        }
    }

    /// <summary>Sends the pending chunk to the engine. The flush is journaled, so a restart cuts the chunks where the live run did and the revisions are the same.</summary>
    public void LiveFlush() { lock (Gate) { if (live is not null && live.PendingRows > 0) Accept(new Entry.Flush()); } }

    void LiveApply(string source, string text, string position)
    {
        var env = CaptureReader.Parse(text, "eventhub:" + source, 0)!;
        if (KeyOf(text) is { } key && liveKeys.Add(key)) { liveKeyOrder.Enqueue(key); if (liveKeyOrder.Count > LiveKeyWindow) liveKeys.Remove(liveKeyOrder.Dequeue()); }
        sourcePositions[source] = position;
        live!.Process(env, pace: false);
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
    public const int SealFormat = 3;
    void MaybeSeal() { if (options.SealEvery > 0 && journal is not null && phase != Phase.Replaying && journalPos - lastSealPosition >= options.SealEvery) Seal(); }

    /// <summary>Takes a seal now: the state after the journal entries so far, stored with the journal. Called by the host every `SealEvery` entries.</summary>
    public void Seal()
    {
        lock (Gate)
        {
            if (journal is null || phase == Phase.Replaying) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = StateSnapshot.Save(StateRootOf());
            var meta = new JsonObject
            {
                ["position"] = journalPos, ["format"] = SealFormat, ["build"] = BuildId, ["kernel"] = Kernel.CompiledDigest, ["digest"] = Engine.StateDigest(), ["catalogDigest"] = Catalog.Graph.Digest(), ["generation"] = generation,
                ["tenant"] = options.Tenant, ["lateHorizon"] = options.LateHorizonWindows, ["cluster"] = options.Cluster, ["space"] = Space.Name, ["mode"] = Mode, ["seq"] = Seq,
                ["live"] = liveOptions,
                ["sources"] = Entry.FilesText(sources.Values.Select(f => new SourceFile(f.Path, f.Text))),
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
            var files = Entry.ParseFiles(m["sources"]!);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = StateSnapshot.Open(seal.Data);
            var loadMs = sw.ElapsedMilliseconds;
            host = new SpaceHost(files, options with { Tenant = m["tenant"]!.GetValue<string>(), LateHorizonWindows = HostOptions.StoredLateHorizon(m), Cluster = m["cluster"]!.GetValue<string>(), SpaceName = m["space"]!.GetValue<string>() }, LoadDefinitions(files), store, Phase.Replaying, m["generation"]!.GetValue<int>(), snapshot.Texts);
            if (host.Catalog.Graph.Digest() != m["catalogDigest"]!.GetValue<string>()) { rejected = "the definitions give another catalog than the one sealed"; host.Dispose(); return null; }
            if (m["live"]?.GetValue<string>() is { } liveJson) host.Apply(new Entry.LiveStart(Entry.ParseOptions(liveJson)));
            var root = host.StateRootOf();
            sw.Restart();
            StateSnapshot.Load(snapshot, root);
            loadMs += sw.ElapsedMilliseconds;
            host.Engine.AdoptState(root.Engine);
            host.Seq = root.Seq; host.lambdaSeen = root.LambdaSeen; host.inputRows = root.InputRows; host.generation = root.Generation;
            if (host.Engine.StateDigest() != m["digest"]!.GetValue<string>()) { rejected = "the restored state digest differs from the sealed one"; host.Dispose(); return null; }
            var sealedRevisions = host.Engine.Graphs.Sum(g => g.Head);
            host.kept = null; host.lastSealPosition = seal.Position; host.journalPos = seal.Position;
            var tail = j.JournalRead(seal.Position).ToList();
            sw.Restart();
            var (recomputed, verified, _) = host.Recover(tail, root.Done ? Phase.Finished : Phase.Running, 0, null);
            host.Restart = new Restart(true, seal.Position, tail.Count, sealedRevisions, recomputed, verified, false, null, loadMs, sw.ElapsedMilliseconds);
            return host;
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException or InvalidCastException or ArgumentException or IOException or TypeLoadException or NullReferenceException)
        {
            rejected = e.GetType().Name + ": " + e.Message; host?.Dispose(); return null;
        }
    }

    /// <summary>What a restart loaded and what it recomputed, in words.</summary>
    public string RestoreSummary() => Restart is not { } r ? "started new" : r.FromSeal
        ? $"loaded from the seal at journal entry {r.SealPosition}: state digest and catalog digest checked against the seal; {r.SealedRevisions:N0} revisions of {Engine.Graphs.Count} graphs came from the seal and were not recomputed; replayed the {r.EntriesReplayed:N0} entries after it and recomputed {r.Recomputed:N0} revisions, each compared with the stored log; the {r.SealPosition:N0} entries before the seal were not read (--verify-journal replays and checks them)"
        : $"replayed all {r.EntriesReplayed + 1:N0} journal entries and recomputed {r.Recomputed:N0} revisions of {Engine.Graphs.Count} graphs, each compared with the stored log" + (r.SealChecked ? $"; the state at the seal (entry {r.SealPosition}) equals the seal's digest" : "");
    public string Cluster => options.Cluster;

    /// <summary>Applies the entries read back from the journal, then leaves `Replaying` for `then` (or `Finished`, if the entries end the input). With `verifyDigest`: the state after entry `verifyAt` must have that digest. Returns the revisions recomputed and compared with the stored log, the sum of the heads, and whether the digest was checked.</summary>
    (long Recomputed, long Verified, bool SealChecked) Recover(IEnumerable<string> entries, Phase then, long verifyAt, string? verifyDigest)
    {
        var sealChecked = false;
        try
        {
            var all = entries.ToList();
            for (var i = 0; i < all.Count; i++)
            {
                journalPos++;
                var entry = Entry.Parse(all[i], RowLayout, interner);
                if (entry is Entry.Commit c)
                {
                    var failedBefore = i + 1 < all.Count && Entry.KindOf(all[i + 1]) == 'x';   // the attempt was refused in the live run too and consumed its generation
                    try { CommitTexts(c.Texts, c.Expected); }
                    catch (HostError e) when (failedBefore) { _ = e; }
                }
                else Apply(entry);
                then = Next(then, entry);
                if (verifyDigest is not null && journalPos == verifyAt)
                {
                    var now = Engine.StateDigest();
                    if (now != verifyDigest) throw new InvalidOperationException($"the journal up to entry {verifyAt} reproduces state {now}, the seal at that entry says {verifyDigest}");
                    sealChecked = true;
                }
            }
        }
        finally { phase = then; }
        var view = new DurableView(durable!, generation, () => false);
        foreach (var g in Engine.Graphs)
            if (view.Head(new GraphKey(options.Tenant, g.Name)).Head is var stored && stored != g.Head)
                throw new InvalidOperationException($"the store has revision {stored} of {g.Name}, the journal reproduces {g.Head}");
        return (views.Sum(v => v.Verified), Engine.Graphs.Sum(g => g.Head), sealChecked);
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

    public void Ingest(string source, Batch rows, long seq) => Accept(new Entry.Rows(source, seq, rows));
    public void Advance(long lambdaNs, long seq) => Accept(new Entry.Advance(lambdaNs, seq));
    public bool WantsProgress => Engine.WantsProgress;
    public void Progress(string source, long untilNs, long seq) => Accept(new Entry.Progress(source, untilNs, seq));
    public void RecordLoss(long timeNs, long count, string reason = "sourceLoss") => Accept(new Entry.Loss(timeNs, count, reason));
    public void Finish() => Accept(new Entry.Finish());

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
        if (kind == "draft") Append(new Entry.SequenceStep());   // drafts are not stored; the sequence number they consumed is
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
            Inputs();                                   // refused here, before anything is journaled, when the inputs are not to be had

            var sw = Stopwatch.StartNew(); mutations++;
            Append(new Entry.Commit(expectedRevision, texts));
            var oldSpace = spaceId; spaceId = newSpaceId;
            Engine fresh;
            try { fresh = Reactivate(cand, newSpaceId, EngineOpts(++generation)).Engine; }
            catch (Exception e) { spaceId = oldSpace; Append(new Entry.CommitRefused()); throw new HostError("ActivationRejected", e.Message, 422); }

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
            if (WriteDefinitions && DefinitionsDir is not null && phase != Phase.Replaying) foreach (var e in sources.Values) if (File.Exists(e.Path)) File.WriteAllText(e.Path, e.Text);
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

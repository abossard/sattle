using System.Globalization;
using System.Net;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Sattle.Core;
using Sattle.Runtime;

namespace Sattle.Cli;

/// <summary>The storage port on Azure Cosmos DB for NoSQL, after paper/ch11 section 11.x and research 02 section 4: five containers (`log`, `state`, `identity`, `lens`, `leases`) with hierarchical partition keys led by tenant and graph. The commit point is the creation of the commit record `c:{r20}` in one transactional batch (a create fails with 409 when the revision exists, which is the conditional write); the state container is the materialization, one idempotent batch per element partition (update: history row and `cur` replaced under If-Match). Reads apply Theorem 11.3 in its simplest form: the commit call returns after materialization, so reads never overlay.
/// Authentication: Entra (`DefaultAzureCredential`) for a real account. The emulator's well-known local key is used only for a localhost endpoint. No key is ever read from a file.</summary>
public sealed class CosmosGraphStore : IGraphStore, IJournalStore
{
    /// <summary>The Azure Cosmos DB emulator's well-known local key. It is public, documented by Microsoft, and valid only for the emulator on localhost. It is not a secret and must never be used for an account.</summary>
    public const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    public const int BatchLimit = 100;
    /// <summary>Largest commit record kept inline, in characters of the edits JSON. Cosmos items and transactional batches are limited to 2 MB; a larger commit is cut into chunk items `p:{r20}:{i}` written first, and the commit record that names them is created last (it stays the single commit point). Settable so a test can force the chunk path with small data.</summary>
    public int InlineLimit { get; set; } = 1_000_000;
    public int ChunkChars { get; set; } = 400_000;
    /// <summary>Fault injection for tests: called after the chunks of a revision are written and before its commit record is created.</summary>
    public Action<long>? BeforeCommitRecord { get; set; }
    /// <summary>Lease length; a writer renews at half. Settable so a test can see a lease expire.</summary>
    public int LeaseMillis { get; set; } = 30_000;

    readonly CosmosClient client; readonly Database db; readonly bool dropOnDispose;
    readonly Container log, state, identity, leases;
    readonly object gate = new();
    sealed class Mirror { public readonly Graph Graph = new(); public long Head; public bool Loaded; public readonly Dictionary<string, long> From = new(StringComparer.Ordinal); public int SinceWatermark; }
    readonly Dictionary<GraphKey, Mirror> mirrors = [];
    readonly Dictionary<string, (int Calls, double Charge)> charges = [];

    /// <summary>Request charge by operation kind as reported by the service. The vNext emulator reports 1 per request (no RU accounting), so numbers from it count requests, not RU.</summary>
    public IReadOnlyDictionary<string, (int Calls, double Charge)> Charges { get { lock (charges) return new Dictionary<string, (int, double)>(charges); } }
    void Charge(string kind, double ru) { lock (charges) { var c = charges.GetValueOrDefault(kind); charges[kind] = (c.Calls + 1, c.Charge + ru); } }

    public static CosmosGraphStore Open(string endpoint, string database, bool dropOnDispose = false)
    {
        var uri = new Uri(endpoint);
        var options = new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true, SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.Default } };
        var client = uri.IsLoopback ? new CosmosClient(endpoint, EmulatorKey, options) : new CosmosClient(endpoint, new DefaultAzureCredential(), options);
        return new CosmosGraphStore(client, database, dropOnDispose);
    }

    public CosmosGraphStore(CosmosClient client, string database, bool dropOnDispose)
    {
        this.client = client; this.dropOnDispose = dropOnDispose;
        db = client.CreateDatabaseIfNotExistsAsync(database).GetAwaiter().GetResult().Database;
        Container Make(string name, string a, string b, string c) => db.CreateContainerIfNotExistsAsync(new ContainerProperties(name, new List<string> { a, b, c })).GetAwaiter().GetResult().Container;
        log = Make("log", "/t", "/g", "/s"); state = Make("state", "/t", "/g", "/k"); identity = Make("identity", "/t", "/g", "/d");
        leases = Make("leases", "/t", "/g", "/n");
    }

    static string R20(long r) => r.ToString("D20", CultureInfo.InvariantCulture);
    static string Seg(long rev) => "seg:" + (rev >> 16).ToString(CultureInfo.InvariantCulture);
    static PartitionKey Pk(GraphKey g, string leaf) => new PartitionKeyBuilder().Add(g.Tenant).Add(g.Graph).Add(leaf).Build();

    static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();

    // ---- mirror: the writer's hot copy of the head (validation of patches before anything is written) ----

    Mirror Get(GraphKey g)
    {
        if (!mirrors.TryGetValue(g, out var m)) mirrors[g] = m = new Mirror();
        if (!m.Loaded) { Refresh(g, m); m.Loaded = true; }      // one writer per graph: the mirror follows its own commits, another writer is found by the conditional create
        return m;
    }

    void Refresh(GraphKey g, Mirror m)
    {
        foreach (var c in FollowRaw(g, m.Head))
        {
            var err = m.Graph.Apply([.. c.Patch.Edits.Where(e => e.Effective)]);
            if (err is not null) throw new InvalidOperationException($"log of {g} is not applicable at revision {c.Patch.To}: {err}");
            foreach (var e in c.Patch.Edits.Where(e => e.Effective)) { if (e.After is null) m.From.Remove(e.Key.ToString()); else m.From[e.Key.ToString()] = c.Patch.To; }
            m.Head = c.Patch.To;
        }
    }

    // ---- port ----

    public HeadInfo Head(GraphKey g)
    {
        lock (gate)
        {
            var m = Get(g);
            if (!HoldsLease(g)) Refresh(g, m);      // a reader follows the log of the writer that holds the lease
            return new HeadInfo(m.Head, m.Head);
        }
    }

    // ---- writer lease: one writer per graph (Ch. 11, `log` meta `lease`). The mirror assumes it; the conditional create alone would also catch a second writer, but only after it had done the work. ----

    readonly string owner = Guid.NewGuid().ToString("N");
    readonly Dictionary<GraphKey, long> leaseUntil = [];
    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    bool HoldsLease(GraphKey g) => leaseUntil.TryGetValue(g, out var until) && until - NowMs() > LeaseMillis / 6;

    /// <summary>Takes or renews the lease of a graph. False when another writer holds an unexpired lease. A newly taken lease first catches the mirror up with the log of the previous holder.</summary>
    bool EnsureLease(GraphKey g)
    {
        var now = NowMs();
        if (leaseUntil.TryGetValue(g, out var until) && until - now > LeaseMillis / 2) return true;
        var doc = ReadDoc(leases, g, "lease", "lease", out var etag);
        if (doc is not null && (string)doc["owner"]! != owner && (long)doc["expires"]! > now) return false;
        var next = new JObject { ["id"] = "lease", ["t"] = g.Tenant, ["g"] = g.Graph, ["n"] = "lease", ["owner"] = owner, ["expires"] = now + LeaseMillis };
        using var res = doc is null
            ? Wait(leases.CreateItemStreamAsync(Stream(next), Pk(g, "lease")))
            : Wait(leases.ReplaceItemStreamAsync(Stream(next), "lease", Pk(g, "lease"), new ItemRequestOptions { IfMatchEtag = etag }));
        Charge("meta", res.Headers.RequestCharge);
        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed) return false;     // another writer was faster
        res.EnsureSuccessStatusCode();
        var fresh = !leaseUntil.ContainsKey(g);
        leaseUntil[g] = now + LeaseMillis;
        if (fresh || doc is not null && (string)doc["owner"]! != owner) Refresh(g, Get(g));
        return true;
    }

    void ReleaseLeases()
    {
        foreach (var g in leaseUntil.Keys.ToList())
        {
            try
            {
                var doc = ReadDoc(leases, g, "lease", "lease", out var etag);
                if (doc is not null && (string)doc["owner"]! == owner) Wait(leases.DeleteItemStreamAsync("lease", Pk(g, "lease"), new ItemRequestOptions { IfMatchEtag = etag })).Dispose();
            }
            catch (CosmosException) { /* the lease expires on its own */ }
        }
        leaseUntil.Clear();
    }

    public CommitResult Commit(CommitBatch b)
    {
        lock (gate)
        {
            var m = Get(b.Graph);
            if (!EnsureLease(b.Graph)) return new CommitResult.Rejected($"another writer holds the lease of {b.Graph.Graph} (at most {LeaseMillis / 1000} s)");
            if (m.Head != b.ExpectedHead) return new CommitResult.Conflict(m.Head);
            var applied = new List<Patch>();
            for (var i = 0; i < b.Patches.Count; i++)
            {
                var p = b.Patches[i];
                var effective = p.Edits.Where(e => e.Effective).ToList();
                if (p.From != m.Head + i || p.To != p.From + 1 || effective.Count == 0) { Undo(m, applied); return new CommitResult.Rejected($"patch {i} has revisions {p.From}->{p.To}, expected {m.Head + i}->{m.Head + i + 1}, or is empty"); }
                var err = m.Graph.Apply(effective);
                if (err is not null) { Undo(m, applied); return new CommitResult.Rejected(err); }
                applied.Add(p);
            }
            // commit point: the commit records, one transactional batch per segment (at most 100 per batch)
            var records = applied.Select((p, i) => (Rev: p.To, Doc: CommitDoc(b.Graph, p, b.Frontiers[i], out var chunks), Chunks: chunks)).ToList();
            try
            {
                foreach (var seg in records.GroupBy(r => Seg(r.Rev)))
                    foreach (var chunk in SplitBySize(seg))
                    {
                        foreach (var r in chunk)
                        {
                            foreach (var c in r.Chunks) { using var cr = Wait(log.UpsertItemStreamAsync(Stream(c), Pk(b.Graph, seg.Key))); Charge("commit", cr.Headers.RequestCharge); cr.EnsureSuccessStatusCode(); }
                            BeforeCommitRecord?.Invoke(r.Rev);
                        }
                        var batch = log.CreateTransactionalBatch(Pk(b.Graph, seg.Key));
                        foreach (var (_, doc, _) in chunk) batch.CreateItem(doc);
                        using var res = Wait(batch.ExecuteAsync());
                        Charge("commit", res.RequestCharge);
                        if (res.StatusCode == HttpStatusCode.Conflict) { Undo(m, applied); Refresh(b.Graph, m); return new CommitResult.Conflict(m.Head); }   // another writer created the revision
                        if (!res.IsSuccessStatusCode) { Undo(m, applied); throw new InvalidOperationException($"commit batch failed: {res.StatusCode} {res.ErrorMessage}"); }
                    }
            }
            catch (CosmosException e) { Undo(m, applied); throw new InvalidOperationException("commit failed: " + e.Message, e); }
            // materialization: one idempotent batch per element partition (never blocks reads: this call returns after it, so there is no overlay)
            MaterializeAll(b.Graph, m, applied);
            foreach (var domain in b.Bindings.GroupBy(x => x.Domain))
                foreach (var chunk in domain.Chunk(BatchLimit))
                {
                    var docs = chunk.Select(bind => new JObject { ["id"] = bind.KeyHash, ["t"] = b.Graph.Tenant, ["g"] = b.Graph.Graph, ["d"] = bind.Domain, ["entity"] = bind.Entity, ["components"] = bind.ComponentsJson }).ToList();
                    var batch = identity.CreateTransactionalBatch(Pk(b.Graph, domain.Key));
                    foreach (var d in docs) batch.CreateItem(d);
                    using var res = Wait(batch.ExecuteAsync());
                    Charge("identity", res.RequestCharge);
                    if (res.IsSuccessStatusCode) continue;
                    foreach (var d in docs)                                // a retry after a crash: some bindings exist already (409 is fine)
                    {
                        using var r = Wait(identity.CreateItemStreamAsync(Stream(d), Pk(b.Graph, domain.Key)));
                        Charge("identity", r.Headers.RequestCharge);
                        if (r.StatusCode != HttpStatusCode.Conflict) r.EnsureSuccessStatusCode();
                    }
                }
            m.Head += applied.Count;
            // the watermark is a hint (reads never overlay: this call returns after materialization), so it is written every 64 commits and when the store is disposed
            if (++m.SinceWatermark >= 64) WriteWatermark(b.Graph, m);
            return new CommitResult.Committed(m.Head);
        }
    }

    void WriteWatermark(GraphKey g, Mirror m)
    {
        Upsert(leases, g, "wm", new JObject { ["id"] = "wm", ["t"] = g.Tenant, ["g"] = g.Graph, ["n"] = "wm", ["rev"] = m.Head });
        m.SinceWatermark = 0;
    }

    // ---- materialization: the edits of a commit grouped by logical partition, one transactional batch per partition. The mirror knows the revision each element's current version began (no read of `cur`), the version row of an element and the adjacency rows of an entity share a partition (they are written together), and every row id is derived from the revision, so a closed interval is replaced by id (no query). ----

    sealed record MatOp(char Kind, string Id, JObject? Doc, long Rev, Edit Origin);
    sealed class PartitionWork { public readonly List<MatOp> Ops = []; public readonly List<(long Rev, Edit Origin)> Origins = []; public bool Slow; }

    void MaterializeAll(GraphKey g, Mirror m, List<Patch> applied)
    {
        var work = new Dictionary<string, PartitionWork>(StringComparer.Ordinal); var order = new List<string>();
        var overlay = new Dictionary<string, long?>(StringComparer.Ordinal);
        long? FromOf(string k) => overlay.TryGetValue(k, out var f) ? f : m.From.TryGetValue(k, out var v) ? v : null;
        PartitionWork W(string leaf) { if (!work.TryGetValue(leaf, out var w)) { work[leaf] = w = new PartitionWork(); order.Add(leaf); } return w; }
        string AdjId(string dir, string rel, long from) => $"a:{dir}:{rel}@{R20(from)}";
        foreach (var p in applied)
            foreach (var e in p.Edits.Where(x => x.Effective))
            {
                var k = e.Key.ToString(); var rev = p.To; var oldFrom = FromOf(k); var wk = W(k); wk.Origins.Add((rev, e));
                if (e.Before is null && e.After is not null) wk.Ops.Add(new('c', "", Ver(g, k, rev, null, e.After), rev, e));
                else if (e.Before is not null)
                {
                    if (oldFrom is null) { wk.Slow = true; }          // the mirror does not know it: the idempotent path reads it
                    else
                    {
                        wk.Ops.Add(new('c', "", Ver(g, k, oldFrom.Value, rev, e.Before), rev, e));
                        if (e.After is null) wk.Ops.Add(new('d', "cur", null, rev, e)); else wk.Ops.Add(new('r', "cur", Ver(g, k, rev, null, e.After), rev, e));
                    }
                }
                overlay[k] = e.After is null ? null : rev;
                if (e.Before is Rel br)
                    foreach (var (ep, dir) in new[] { (br.Source, "out"), (br.Target, "in") })
                    {
                        var we = W("e:" + ep); we.Origins.Add((rev, e));
                        if (oldFrom is null) { we.Slow = true; continue; }
                        we.Ops.Add(new('r', AdjId(dir, br.Id, oldFrom.Value), Adj(g, ep, br, dir, oldFrom.Value, rev), rev, e));
                    }
                if (e.After is Rel ar)
                    foreach (var (ep, dir) in new[] { (ar.Source, "out"), (ar.Target, "in") })
                    { var we = W("e:" + ep); we.Origins.Add((rev, e)); we.Ops.Add(new('c', "", Adj(g, ep, ar, dir, rev, null), rev, e)); }
            }
        var redo = new HashSet<(long, Edit)>(); var redoGate = new object();
        // the partitions are independent: their batches run concurrently (at most 16 in flight)
        Parallel.ForEach(order, new ParallelOptions { MaxDegreeOfParallelism = 16 }, leaf =>
        {
            var w = work[leaf]; var ok = !w.Slow;
            if (ok)
                foreach (var chunk in w.Ops.Chunk(BatchLimit))
                {
                    var batch = state.CreateTransactionalBatch(Pk(g, leaf));
                    foreach (var op in chunk) { if (op.Kind == 'c') batch.CreateItem(op.Doc!); else if (op.Kind == 'r') batch.ReplaceItem(op.Id, op.Doc!); else batch.DeleteItem(op.Id); }
                    using var res = Wait(batch.ExecuteAsync());
                    Charge("materialize", res.RequestCharge);
                    if (!res.IsSuccessStatusCode) { ok = false; break; }
                }
            if (!ok) lock (redoGate) foreach (var o in w.Origins) redo.Add(o);
        });
        // a retry after a crash, or a state the mirror could not know: the idempotent path, per edit, in revision order
        foreach (var (rev, e) in redo.OrderBy(x => x.Item1)) Materialize(g, rev, e);
        foreach (var (k, v) in overlay) { if (v is null) m.From.Remove(k); else m.From[k] = v.Value; }
    }

    static void Undo(Mirror m, List<Patch> applied)
    {
        for (var i = applied.Count - 1; i >= 0; i--) m.Graph.Apply([.. applied[i].Edits.Where(e => e.Effective).Select(e => e.Inverted())]);
        applied.Clear();
    }

    /// <summary>Records of one segment cut into transactional batches of at most 100 items and about 1.5 MB.</summary>
    IEnumerable<List<(long Rev, JObject Doc, List<JObject> Chunks)>> SplitBySize(IEnumerable<(long Rev, JObject Doc, List<JObject> Chunks)> records)
    {
        var cur = new List<(long, JObject, List<JObject>)>(); long size = 0;
        foreach (var r in records)
        {
            var n = r.Doc.ToString(Newtonsoft.Json.Formatting.None).Length;
            if (cur.Count > 0 && (cur.Count >= BatchLimit || size + n > 1_500_000)) { yield return cur; cur = []; size = 0; }
            cur.Add(r); size += n;
        }
        if (cur.Count > 0) yield return cur;
    }

    JObject CommitDoc(GraphKey g, Patch p, Frontier f, out List<JObject> chunks)
    {
        var edits = ElementJson.EncodeEdits(p.Edits.Where(e => e.Effective));
        chunks = [];
        var doc = new JObject
        {
            ["id"] = "c:" + R20(p.To), ["t"] = g.Tenant, ["g"] = g.Graph, ["s"] = Seg(p.To), ["kind"] = "commit", ["rev"] = p.To, ["graph"] = p.Graph,
            ["cause"] = new JArray(p.Cause.Inputs.Select(x => (object)x)), ["frontier"] = ElementJson.EncodeFrontier(f),
        };
        if (edits.Length <= InlineLimit) { doc["edits"] = edits; return doc; }
        doc["chunks"] = (edits.Length + ChunkChars - 1) / ChunkChars;
        for (var i = 0; i * ChunkChars < edits.Length; i++)
            chunks.Add(new JObject { ["id"] = $"p:{R20(p.To)}:{i:D5}", ["t"] = g.Tenant, ["g"] = g.Graph, ["s"] = Seg(p.To), ["kind"] = "chunk", ["rev"] = p.To, ["i"] = i, ["data"] = edits.Substring(i * ChunkChars, Math.Min(ChunkChars, edits.Length - i * ChunkChars)) });
        return doc;
    }

    string EditsOf(GraphKey g, JObject d)
    {
        if (d["chunks"] is not { } nc) return (string)d["edits"]!;
        var rev = (long)d["rev"]!; var n = (int)nc;
        var it = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.i, c.data FROM c WHERE c.kind = 'chunk' AND c.rev = @r ORDER BY c.i").WithParameter("@r", rev), requestOptions: new QueryRequestOptions { PartitionKey = Pk(g, Seg(rev)) });
        var sb = new System.Text.StringBuilder(); var got = 0;
        while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("follow", page.RequestCharge); foreach (var c in page) if (got < n) { sb.Append((string)c["data"]!); got++; } }
        return got == n ? sb.ToString() : throw new InvalidOperationException($"commit record {rev} of {g.Graph} names {n} chunks, {got} found");
    }

    static MemoryStream Stream(JObject o) => new(System.Text.Encoding.UTF8.GetBytes(o.ToString(Newtonsoft.Json.Formatting.None)));

    void Upsert(Container c, GraphKey g, string leaf, JObject doc)
    {
        using var r = Wait(c.UpsertItemStreamAsync(Stream(doc), Pk(g, leaf)));
        Charge("meta", r.Headers.RequestCharge);
    }

    JObject? ReadDoc(Container c, GraphKey g, string leaf, string id, out string? etag)
    {
        using var r = Wait(c.ReadItemStreamAsync(id, Pk(g, leaf)));
        Charge("read", r.Headers.RequestCharge);
        etag = r.Headers.ETag;
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        using var sr = new StreamReader(r.Content);
        return JObject.Parse(sr.ReadToEnd());
    }

    JObject Ver(GraphKey g, string k, long from, long? to, Element state) => new()
    {
        ["id"] = to is null ? "cur" : "v@" + R20(from), ["t"] = g.Tenant, ["g"] = g.Graph, ["k"] = k, ["kind"] = "ver", ["rf"] = from, ["open"] = to is null, ["rt"] = to ?? 0L, ["state"] = ElementJson.Encode(state),
    };

    JObject Adj(GraphKey g, string endpoint, Rel r, string dir, long from, long? to) => new()
    {
        ["id"] = $"a:{dir}:{r.Id}@{R20(from)}", ["t"] = g.Tenant, ["g"] = g.Graph, ["k"] = "e:" + endpoint, ["kind"] = "adj", ["rel"] = r.Id, ["other"] = dir == "out" ? r.Target : r.Source, ["type"] = r.Type, ["dir"] = dir, ["rf"] = from, ["open"] = to is null, ["rt"] = to ?? 0L,
    };

    /// <summary>One idempotent batch per element partition. The skip test of Theorem 11.4: an update or create finds `cur.from = r`, a delete finds no `cur` and a history row with `to = r`.</summary>
    void Materialize(GraphKey g, long rev, Edit e)
    {
        var k = e.Key.ToString();
        var pk = Pk(g, k);
        var cur = ReadDoc(state, g, k, "cur", out var etag);
        long? oldFrom = cur is null ? null : (long)cur["rf"]!;
        if (oldFrom == rev && e.After is not null) { /* already materialized */ }
        else if (e.Before is null && e.After is not null)
        {
            var batch = state.CreateTransactionalBatch(pk); batch.CreateItem(Ver(g, k, rev, null, e.After));
            Run(batch, "materialize");
        }
        else if (cur is not null)
        {
            var batch = state.CreateTransactionalBatch(pk);
            batch.CreateItem(Ver(g, k, oldFrom!.Value, rev, e.Before!));
            if (e.After is null) batch.DeleteItem("cur", new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
            else batch.ReplaceItem("cur", Ver(g, k, rev, null, e.After), new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
            Run(batch, "materialize");
        }
        // relationship edits also write the adjacency items in both endpoint partitions
        if (e.Before is Rel br) foreach (var (ep, dir) in new[] { (br.Source, "out"), (br.Target, "in") }) CloseAdjacency(g, br, ep, dir, oldFrom ?? rev, rev);
        if (e.After is Rel ar && oldFrom != rev) foreach (var (ep, dir) in new[] { (ar.Source, "out"), (ar.Target, "in") }) OpenAdjacency(g, ar, ep, dir, rev);
    }

    void OpenAdjacency(GraphKey g, Rel r, string endpoint, string dir, long rev)
    {
        var doc = Adj(g, endpoint, r, dir, rev, null);
        using var res = Wait(state.CreateItemStreamAsync(Stream(doc), Pk(g, "e:" + endpoint)));
        Charge("materialize", res.Headers.RequestCharge);
        if (res.StatusCode != HttpStatusCode.Conflict) res.EnsureSuccessStatusCode();
    }

    void CloseAdjacency(GraphKey g, Rel r, string endpoint, string dir, long from, long rev)
    {
        // the open adjacency item of this relationship in this endpoint partition (the interval may have opened at any earlier revision)
        var it = state.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT * FROM c WHERE c.kind = 'adj' AND c.rel = @r AND c.dir = @d AND c.open = true").WithParameter("@r", r.Id).WithParameter("@d", dir),
            requestOptions: new QueryRequestOptions { PartitionKey = Pk(g, "e:" + endpoint) });
        var open = new List<JObject>();
        while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("query", page.RequestCharge); open.AddRange(page); }
        _ = from;
        foreach (var d in open)
        {
            d["open"] = false; d["rt"] = rev;
            using var res = Wait(state.ReplaceItemStreamAsync(Stream(d), (string)d["id"]!, Pk(g, "e:" + endpoint)));
            Charge("materialize", res.Headers.RequestCharge);
            res.EnsureSuccessStatusCode();
        }
    }

    void Run(TransactionalBatch batch, string kind)
    {
        using var res = Wait(batch.ExecuteAsync());
        Charge(kind, res.RequestCharge);
        if (res.StatusCode == HttpStatusCode.Conflict && kind == "materialize") return;     // an earlier attempt already wrote it (idempotent)
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{kind} batch failed: {res.StatusCode} {res.ErrorMessage}");
    }

    public IReadOnlyList<ReadResult> Read(GraphKey g, long revision, IReadOnlyList<ElementKey> keys)
    {
        lock (gate)
        {
            if (revision > Get(g).Head) return [.. keys.Select(_ => (ReadResult)new ReadResult.Unavailable())];
            var res = new List<ReadResult>();
            foreach (var key in keys)
            {
                var it = state.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT * FROM c WHERE c.kind = 'ver' AND c.rf <= @r AND (c.open = true OR c.rt > @r)").WithParameter("@r", revision),
                    requestOptions: new QueryRequestOptions { PartitionKey = Pk(g, key.ToString()) });
                JObject? hit = null;
                while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("read", page.RequestCharge); hit ??= page.FirstOrDefault(); }
                res.Add(hit is null ? new ReadResult.Absent() : new ReadResult.Present(ElementJson.Decode((string)hit["state"]!), (long)hit["rf"]!, (bool)hit["open"]! ? null : (long)hit["rt"]!));
            }
            return res;
        }
    }

    public AdjacencyPage ScanAdjacency(GraphKey g, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation)
    {
        lock (gate)
        {
            var q = "SELECT * FROM c WHERE c.kind = 'adj' AND c.dir = @d AND c.rf <= @r AND (c.open = true OR c.rt > @r)" + (type is null ? "" : " AND c.type = @ty") + (continuation is null ? "" : " AND c.rel > @c") + " ORDER BY c.rel";
            var def = new QueryDefinition(q).WithParameter("@d", dir == Direction.Out ? "out" : "in").WithParameter("@r", revision);
            if (type is not null) def = def.WithParameter("@ty", type);
            if (continuation is not null) def = def.WithParameter("@c", continuation);
            var it = state.GetItemQueryIterator<JObject>(def, requestOptions: new QueryRequestOptions { PartitionKey = Pk(g, "e:" + endpoint), MaxItemCount = pageSize + 1 });
            var all = new List<AdjEntry>();
            while (it.HasMoreResults && all.Count <= pageSize)
            {
                var page = Wait(it.ReadNextAsync()); Charge("query", page.RequestCharge);
                all.AddRange(page.Select(d => new AdjEntry((string)d["rel"]!, (string)d["other"]!, (string)d["type"]!, dir)));
            }
            var pageEntries = all.Take(pageSize).ToList();
            return new AdjacencyPage(pageEntries, all.Count > pageSize ? pageEntries[^1].RelId : null);
        }
    }

    IEnumerable<CommittedPatch> FollowRaw(GraphKey g, long after)
    {
        var res = new List<CommittedPatch>();
        var it = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT * FROM c WHERE c.t = @t AND c.g = @g AND c.kind = 'commit' AND c.rev > @a ORDER BY c.rev").WithParameter("@t", g.Tenant).WithParameter("@g", g.Graph).WithParameter("@a", after));
        while (it.HasMoreResults)
        {
            var page = Wait(it.ReadNextAsync()); Charge("follow", page.RequestCharge);
            foreach (var d in page)
            {
                var rev = (long)d["rev"]!;
                res.Add(new CommittedPatch(new Patch((string)d["graph"]!, rev - 1, rev, ElementJson.DecodeEdits(EditsOf(g, d)), new Cause([.. ((JArray)d["cause"]!).Select(x => (string)x!)], "0")), ElementJson.DecodeFrontier((string)d["frontier"]!)));
            }
        }
        return res;
    }

    public IEnumerable<CommittedPatch> Follow(GraphKey g, long after) { lock (gate) return FollowRaw(g, after); }

    Graph StateAt(GraphKey g, long rev)
    {
        var edits = new List<Edit>();
        var it = state.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT * FROM c WHERE c.t = @t AND c.g = @g AND c.kind = 'ver' AND c.rf <= @r AND (c.open = true OR c.rt > @r)").WithParameter("@t", g.Tenant).WithParameter("@g", g.Graph).WithParameter("@r", rev));
        while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("scan", page.RequestCharge); edits.AddRange(page.Select(d => Edit.Create(ElementJson.Decode((string)d["state"]!)))); }
        var gr = new Graph();
        var err = gr.Apply(edits);
        return err is null ? gr : throw new InvalidOperationException(err);
    }

    public SealInfo Seal(GraphKey g, long revision)
    {
        lock (gate)
        {
            var gr = StateAt(g, revision);
            var fr = Frontier.Empty;
            if (revision > 0 && ReadDoc(log, g, Seg(revision), "c:" + R20(revision), out _) is { } c) fr = ElementJson.DecodeFrontier((string)c["frontier"]!);
            return new SealInfo(revision, gr.Digest(), gr.Count, fr);
        }
    }

    public IEnumerable<Element> OpenSnapshot(GraphKey g, long revision) { lock (gate) { return [.. StateAt(g, revision).Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)]; } }

    public long BindingCount(GraphKey g)
    {
        var it = identity.GetItemQueryIterator<long>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.t = @t AND c.g = @g").WithParameter("@t", g.Tenant).WithParameter("@g", g.Graph));
        long n = 0; while (it.HasMoreResults) foreach (var x in Wait(it.ReadNextAsync())) n += x;
        return n;
    }

    // ---- the journal of a served space (IJournalStore): items `j:{n20}` of the `log` container under the tenant and graph "journal"; a body above the inline limit is cut into chunk items written first ----

    static readonly GraphKey JournalKey = new("journal", "journal");
    long journalNext = -1;

    long NextJournal()
    {
        if (journalNext >= 0) return journalNext;
        var it = log.GetItemQueryIterator<long>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.t = 'journal' AND c.g = 'journal' AND c.kind = 'j'"));
        long n = 0; while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("follow", page.RequestCharge); foreach (var x in page) n += x; }
        return journalNext = n;
    }

    public long JournalCount { get { lock (gate) return NextJournal(); } }

    public void JournalAppend(string body)
    {
        lock (gate)
        {
            if (!EnsureLease(JournalKey)) throw new InvalidOperationException("another writer holds the journal of this database");
            var n = NextJournal() + 1; var seg = "seg:" + (n >> 16).ToString(CultureInfo.InvariantCulture);
            var doc = new JObject { ["id"] = "j:" + R20(n), ["t"] = "journal", ["g"] = "journal", ["s"] = seg, ["kind"] = "j", ["n"] = n };
            if (body.Length <= InlineLimit) doc["body"] = body;
            else
            {
                doc["chunks"] = (body.Length + ChunkChars - 1) / ChunkChars;
                for (var i = 0; i * ChunkChars < body.Length; i++)
                {
                    var c = new JObject { ["id"] = $"q:{R20(n)}:{i:D5}", ["t"] = "journal", ["g"] = "journal", ["s"] = seg, ["kind"] = "jchunk", ["n"] = n, ["i"] = i, ["data"] = body.Substring(i * ChunkChars, Math.Min(ChunkChars, body.Length - i * ChunkChars)) };
                    using var cr = Wait(log.UpsertItemStreamAsync(Stream(c), Pk(JournalKey, seg))); Charge("journal", cr.Headers.RequestCharge); cr.EnsureSuccessStatusCode();
                }
            }
            using var res = Wait(log.CreateItemStreamAsync(Stream(doc), Pk(JournalKey, seg)));
            Charge("journal", res.Headers.RequestCharge);
            if (res.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException($"journal entry {n} exists: another writer");
            res.EnsureSuccessStatusCode();
            journalNext = n;
        }
    }

    public IEnumerable<string> JournalRead(long after = 0)
    {
        lock (gate)
        {
            var res = new List<string>();
            var it = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT * FROM c WHERE c.t = 'journal' AND c.g = 'journal' AND c.kind = 'j' AND c.n > @a ORDER BY c.n").WithParameter("@a", after));
            while (it.HasMoreResults)
            {
                var page = Wait(it.ReadNextAsync()); Charge("journal", page.RequestCharge);
                foreach (var d in page)
                {
                    if (d["chunks"] is not { } nc) { res.Add((string)d["body"]!); continue; }
                    var n = (long)d["n"]!; var seg = "seg:" + (n >> 16).ToString(CultureInfo.InvariantCulture); var sb = new System.Text.StringBuilder();
                    var ci = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.i, c.data FROM c WHERE c.kind = 'jchunk' AND c.n = @n ORDER BY c.i").WithParameter("@n", n), requestOptions: new QueryRequestOptions { PartitionKey = Pk(JournalKey, seg) });
                    var got = 0; while (ci.HasMoreResults) { var cp = Wait(ci.ReadNextAsync()); Charge("journal", cp.RequestCharge); foreach (var c in cp) if (got < (int)nc) { sb.Append((string)c["data"]!); got++; } }
                    res.Add(got == (int)nc ? sb.ToString() : throw new InvalidOperationException($"journal entry {n} names {nc} chunks, {got} found"));
                }
            }
            return res;
        }
    }

    // ---- seals: the compressed snapshot cut into base64 chunk items (`s:{pos}:{i}`), then the seal item that names them (the commit point of a seal) ----

    const int SealChunkChars = 700_000;

    public void SealPut(long position, string meta, byte[] data)
    {
        lock (gate)
        {
            if (!EnsureLease(JournalKey)) throw new InvalidOperationException("another writer holds the journal of this database");
            var text = Convert.ToBase64String(data); var chunks = (text.Length + SealChunkChars - 1) / SealChunkChars;
            for (var i = 0; i < chunks; i++)
            {
                var c = new JObject { ["id"] = $"s:{R20(position)}:{i:D5}", ["t"] = "journal", ["g"] = "journal", ["s"] = "seal", ["kind"] = "sealchunk", ["pos"] = position, ["i"] = i, ["data"] = text.Substring(i * SealChunkChars, Math.Min(SealChunkChars, text.Length - i * SealChunkChars)) };
                using var cr = Wait(log.UpsertItemStreamAsync(Stream(c), Pk(JournalKey, "seal"))); Charge("seal", cr.Headers.RequestCharge); cr.EnsureSuccessStatusCode();
            }
            var head = new JObject { ["id"] = "s:" + R20(position), ["t"] = "journal", ["g"] = "journal", ["s"] = "seal", ["kind"] = "seal", ["pos"] = position, ["meta"] = meta, ["chunks"] = chunks };
            using var hr = Wait(log.UpsertItemStreamAsync(Stream(head), Pk(JournalKey, "seal"))); Charge("seal", hr.Headers.RequestCharge); hr.EnsureSuccessStatusCode();
            // keep the latest two
            var old = new List<JObject>();
            var it = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.id, c.pos, c.chunks FROM c WHERE c.kind = 'seal' ORDER BY c.pos DESC"), requestOptions: new QueryRequestOptions { PartitionKey = Pk(JournalKey, "seal") });
            while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("seal", page.RequestCharge); old.AddRange(page); }
            foreach (var o in old.Skip(2))
            {
                var pos = (long)o["pos"]!; var n = (int)o["chunks"]!;
                for (var i = 0; i < n; i++) Wait(log.DeleteItemStreamAsync($"s:{R20(pos)}:{i:D5}", Pk(JournalKey, "seal"))).Dispose();
                Wait(log.DeleteItemStreamAsync((string)o["id"]!, Pk(JournalKey, "seal"))).Dispose();
            }
        }
    }

    public SealRecord? SealLatest()
    {
        lock (gate)
        {
            var it = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT TOP 1 * FROM c WHERE c.kind = 'seal' ORDER BY c.pos DESC"), requestOptions: new QueryRequestOptions { PartitionKey = Pk(JournalKey, "seal") });
            JObject? head = null;
            while (it.HasMoreResults) { var page = Wait(it.ReadNextAsync()); Charge("seal", page.RequestCharge); head ??= page.FirstOrDefault(); }
            if (head is null) return null;
            var pos = (long)head["pos"]!; var n = (int)head["chunks"]!; var sb = new System.Text.StringBuilder();
            var ci = log.GetItemQueryIterator<JObject>(new QueryDefinition("SELECT c.i, c.data FROM c WHERE c.kind = 'sealchunk' AND c.pos = @p ORDER BY c.i").WithParameter("@p", pos), requestOptions: new QueryRequestOptions { PartitionKey = Pk(JournalKey, "seal") });
            var got = 0; while (ci.HasMoreResults) { var page = Wait(ci.ReadNextAsync()); Charge("seal", page.RequestCharge); foreach (var c in page) if (got < n) { sb.Append((string)c["data"]!); got++; } }
            return got == n ? new SealRecord(pos, (string)head["meta"]!, Convert.FromBase64String(sb.ToString())) : null;     // an incomplete seal is no seal
        }
    }

    public void Dispose()
    {
        try { foreach (var (g, m) in mirrors) if (m.SinceWatermark > 0 && leaseUntil.ContainsKey(g)) WriteWatermark(g, m); } catch (Exception) { /* the watermark is a hint */ }
        try { ReleaseLeases(); } catch (Exception) { /* the leases expire on their own */ }
        if (dropOnDispose) { try { Wait(db.DeleteAsync()); } catch (CosmosException) { } }
        client.Dispose();
    }
}

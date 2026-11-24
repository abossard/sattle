using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sattle.Core;

namespace Sattle.Runtime;

public readonly record struct GraphKey(string Tenant, string GraphId)
{
    public override string ToString() => Tenant + "/" + GraphId;
}

public sealed record HeadInfo(long Head, long Watermark);
/// <summary>A key bound to an identity, or a merge of two identities (`Domain` "~forward", `KeyHash` the loser's id, `Entity` the survivor's), as the engine hands it to a store: the identity catalog's codes. A store that persists it reads its texts, made when read (`KeyHash` the SHA-256 of the key as hex, `ComponentsJson` the canonical components); a store that does not never makes them.</summary>
public readonly struct IdentityBinding
{
    readonly IdentityCatalog? catalog;
    readonly IdentityCatalog.Key key;
    /// <summary>The identity's code; for a merge, the survivor's. `loser` is -1 unless a merge.</summary>
    readonly int entity, loser;
    readonly string[]? texts;
    /// <summary>A binding as texts, as a store reads it back.</summary>
    public IdentityBinding(string domain, string keyHash, string entity, string componentsJson) { texts = [domain, keyHash, entity, componentsJson]; loser = -1; }
    IdentityBinding(IdentityCatalog catalog, IdentityCatalog.Key key, int entity, int loser) { this.catalog = catalog; this.key = key; this.entity = entity; this.loser = loser; }
    internal static IdentityBinding Bound(IdentityCatalog catalog, IdentityCatalog.Key key, int entity) => new(catalog, key, entity, -1);
    internal static IdentityBinding Forward(IdentityCatalog catalog, int loser, int survivor) => new(catalog, default, survivor, loser);
    public string Domain => texts?[0] ?? (loser >= 0 ? "~forward" : catalog!.DomainId(key));
    public string KeyHash => texts?[1] ?? (loser >= 0 ? catalog!.Text(loser) : catalog!.HashHex(key));
    public string Entity => texts?[2] ?? catalog!.Text(entity);
    public string ComponentsJson => texts?[3] ?? (loser >= 0 ? "[]" : catalog!.ComponentsJson(key));
}
/// <summary>`Bindings` is valid during the commit call: a store that keeps them copies them.</summary>
public sealed record CommitBatch(GraphKey Graph, long ExpectedHead, IReadOnlyList<Patch> Patches, IReadOnlyList<Frontier> Frontiers, IReadOnlyList<IdentityBinding> Bindings);

public abstract record CommitResult
{
    public sealed record Committed(long Head) : CommitResult;
    public sealed record Conflict(long ActualHead) : CommitResult;
    /// <summary>The patch sequence is not applicable (guard failure, uniqueness, wrong revisions). Nothing was written.</summary>
    public sealed record Rejected(string Reason) : CommitResult;
}

public abstract record ReadResult
{
    public sealed record Present(Element State, long From, long? To) : ReadResult;
    public sealed record Absent : ReadResult;
    public sealed record Unavailable : ReadResult;
}

public enum Direction { Out, In }
public sealed record AdjEntry(string RelId, string Other, string Type, Direction Dir);
public sealed record AdjacencyPage(IReadOnlyList<AdjEntry> Entries, string? Continuation);
public sealed record CommittedPatch(Patch Patch, Frontier Frontier);
public sealed record SealInfo(long Revision, string Digest, int Count, Frontier Frontier);

/// <summary>What an engine needs of a store: the head of a graph, and to commit patches after it. Synchronous in this slice: the in-memory and SQLite stores need no awaiting.</summary>
public interface IGraphStore : IDisposable
{
    HeadInfo Head(GraphKey g);
    CommitResult Commit(CommitBatch batch);
}

/// <summary>A store that keeps what it committed: with `Head` and `Commit` the six operations of research 02 section 3.2 (Read, ScanAdjacency, Follow, Seal and OpenSnapshot).</summary>
public interface IGraphHistory : IGraphStore
{
    IReadOnlyList<ReadResult> Read(GraphKey g, long revision, IReadOnlyList<ElementKey> keys);
    AdjacencyPage ScanAdjacency(GraphKey g, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation);
    /// <summary>Committed patches after a revision, in order. Throws GapException when the store no longer has them.</summary>
    IEnumerable<CommittedPatch> Follow(GraphKey g, long after);
    SealInfo Seal(GraphKey g, long revision);
    IEnumerable<Element> OpenSnapshot(GraphKey g, long revision);
}

/// <summary>A store a space can be restarted from: it keeps the journal of the space and the history of its graphs, which a restart compares its recomputed commits with.</summary>
public interface IDurableStore : IGraphHistory, IJournalStore;

public sealed class GapException(long lastAvailable) : Exception($"history before revision {lastAvailable} is not available") { public long LastAvailable { get; } = lastAvailable; }

/// <summary>JSON form of element states (the canonical JCS encoding of Ch. 3), for the SQLite store.</summary>
public static class ElementJson
{
    public static string Encode(Element e) => Jcs.Encode(e);

    public static Element Decode(string json)
    {
        using var d = JsonDocument.Parse(json);
        return Decode(d.RootElement);
    }

    public static Element Decode(JsonElement r)
    {
        if (r.TryGetProperty("digest", out var dg)) return new OntologyState(r.GetProperty("id").GetString()!, r.GetProperty("version").GetString()!, dg.GetString()!);
        var props = Props.Of(r.GetProperty("props").EnumerateObject().Select(p => KeyValuePair.Create(p.Name, DecodeValue(p.Value))));
        return r.TryGetProperty("source", out var s)
            ? new Rel(r.GetProperty("id").GetString()!, s.GetString()!, r.GetProperty("target").GetString()!, r.GetProperty("type").GetString()!, props)
            : new Entity(r.GetProperty("id").GetString()!, r.GetProperty("type").GetString()!, props);
    }

    public static Value DecodeValue(JsonElement v)
    {
        var kind = v.GetProperty("kind").GetString();
        if (kind == "null") return Value.Null;
        var x = v.GetProperty("value");
        return kind switch
        {
            "text" => Value.Text(x.GetString()!),
            "integer" => Value.Integer(BigInteger.Parse(x.GetString()!, CultureInfo.InvariantCulture)),
            "decimal" => Value.Decimal(decimal.Parse(x.GetString()!, CultureInfo.InvariantCulture)),
            "boolean" => Value.Boolean(x.GetBoolean()),
            "instant" => Norm.TryParseInstant(x.GetString()!, out var ns) ? Value.Instant(ns) : throw new FormatException("instant"),
            "entity" => Value.Entity(x.GetString()!),
            "list" => Value.List(x.EnumerateArray().Select(DecodeValue)),
            _ => throw new FormatException(kind),
        };
    }

    /// <summary>Edit list as JSON: [[key, before|null, after|null], ...] in canonical order.</summary>
    public static string EncodeEdits(IEnumerable<Edit> edits)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var e in edits)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('['); Jcs.Str(sb, e.Key.ToString()); sb.Append(',');
            if (e.Before is null) sb.Append("null"); else Jcs.Element(sb, e.Before);
            sb.Append(',');
            if (e.After is null) sb.Append("null"); else Jcs.Element(sb, e.After);
            sb.Append(']');
        }
        return sb.Append(']').ToString();
    }

    public static List<Edit> DecodeEdits(string json)
    {
        using var d = JsonDocument.Parse(json);
        var res = new List<Edit>();
        foreach (var e in d.RootElement.EnumerateArray())
        {
            var ks = e[0].GetString()!;
            var key = ks == "o" ? ElementKey.Ontology : new ElementKey(ks[0], ks[2..]);
            res.Add(new Edit(key, e[1].ValueKind == JsonValueKind.Null ? null : Decode(e[1]), e[2].ValueKind == JsonValueKind.Null ? null : Decode(e[2])));
        }
        return res;
    }

    public static string EncodeFrontier(Frontier f)
    {
        var sb = new StringBuilder("{\"cfg\":");
        Jcs.Str(sb, f.Cfg.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"gin\":{");
        sb.Append(string.Join(",", f.Gin.Select(kv => { var s = new StringBuilder(); Jcs.Str(s, kv.Key); return s + ":" + "\"" + kv.Value.ToString(CultureInfo.InvariantCulture) + "\""; })));
        sb.Append("},\"pos\":{");
        sb.Append(string.Join(",", f.Pos.Select(kv => { var s = new StringBuilder(); Jcs.Str(s, kv.Key); return s + ":" + "\"" + kv.Value.ToString(CultureInfo.InvariantCulture) + "\""; })));
        return sb.Append("}}").ToString();
    }

    public static Frontier DecodeFrontier(string json)
    {
        using var d = JsonDocument.Parse(json);
        var r = d.RootElement;
        ImmutableSortedDictionary<string, long> M(JsonElement e) => e.EnumerateObject().ToImmutableSortedDictionary(p => p.Name, p => long.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return new Frontier(M(r.GetProperty("pos")), M(r.GetProperty("gin")), long.Parse(r.GetProperty("cfg").GetString()!, CultureInfo.InvariantCulture));
    }
}

/// <summary>In-memory implementation of the port: per graph a commit list, revision-interval element versions and adjacency versions.</summary>
public sealed class InMemoryGraphStore : IDurableStore
{
    readonly InMemoryJournal journal = new();
    public long JournalCount => journal.JournalCount;
    public void JournalAppend(string body) => journal.JournalAppend(body);
    public IEnumerable<string> JournalRead(long after = 0) => journal.JournalRead(after);
    public void SealPut(long position, string meta, byte[] data) => journal.SealPut(position, meta, data);
    public SealRecord? SealLatest() => journal.SealLatest();
    sealed record Version(long From, long? To, Element State);
    sealed class Adj(string rel, string other, string type, Direction dir, long from) { public string Rel = rel, Other = other, Type = type; public Direction Dir = dir; public long From = from; public long? To; }
    sealed class G
    {
        public readonly List<CommittedPatch> Commits = [];
        public readonly Graph Head = new();
        public readonly Dictionary<ElementKey, List<Version>> Versions = [];
        public readonly Dictionary<string, List<Adj>> Adjacency = new(StringComparer.Ordinal);
        public readonly List<IdentityBinding> Bindings = [];
    }
    readonly Dictionary<GraphKey, G> graphs = [];
    readonly object gate = new();
    G Get(GraphKey k) { if (!graphs.TryGetValue(k, out var g)) graphs[k] = g = new G(); return g; }

    public HeadInfo Head(GraphKey g) { lock (gate) { var h = Get(g).Commits.Count; return new HeadInfo(h, h); } }

    public CommitResult Commit(CommitBatch b)
    {
        lock (gate)
        {
            var g = Get(b.Graph);
            var head = g.Commits.Count;
            if (head != b.ExpectedHead) return new CommitResult.Conflict(head);
            var applied = new List<Patch>();
            for (var i = 0; i < b.Patches.Count; i++)
            {
                var p = b.Patches[i];
                var effective = p.Edits.Where(e => e.Effective).ToList();
                if (p.From != head + i || p.To != p.From + 1 || effective.Count == 0) { Undo(g, applied); return new CommitResult.Rejected($"patch {i} has revisions {p.From}->{p.To}, expected {head + i}->{head + i + 1}, or is empty"); }
                var err = g.Head.Apply(effective);
                if (err is not null) { Undo(g, applied); return new CommitResult.Rejected(err); }
                applied.Add(p);
            }
            for (var i = 0; i < applied.Count; i++)
            {
                var p = applied[i]; var rev = p.To;
                foreach (var e in p.Edits.Where(e => e.Effective))
                {
                    var list = g.Versions.TryGetValue(e.Key, out var l) ? l : g.Versions[e.Key] = [];
                    if (e.Before is not null) { var last = list[^1]; list[^1] = last with { To = rev }; if (e.Before is Rel br) CloseAdj(g, br, rev); }
                    if (e.After is not null) { list.Add(new Version(rev, null, e.After)); if (e.After is Rel ar) OpenAdj(g, ar, rev); }
                }
                g.Commits.Add(new CommittedPatch(new Patch(p.GraphId, p.From, p.To, p.Edits.Where(e => e.Effective), p.Cause), b.Frontiers[i]));
            }
            foreach (var x in b.Bindings) g.Bindings.Add(new IdentityBinding(x.Domain, x.KeyHash, x.Entity, x.ComponentsJson));
            return new CommitResult.Committed(g.Commits.Count);
        }
    }

    static void Undo(G g, List<Patch> applied)
    {
        for (var i = applied.Count - 1; i >= 0; i--) g.Head.Apply(applied[i].Edits.Where(e => e.Effective).Select(e => e.Inverted()).ToList());
    }
    static void OpenAdj(G g, Rel r, long rev)
    {
        Add(g, r.Source, new Adj(r.Id, r.Target, r.Type, Direction.Out, rev));
        Add(g, r.Target, new Adj(r.Id, r.Source, r.Type, Direction.In, rev));
    }
    static void Add(G g, string endpoint, Adj a) { if (!g.Adjacency.TryGetValue(endpoint, out var l)) g.Adjacency[endpoint] = l = []; l.Add(a); }
    static void CloseAdj(G g, Rel r, long rev)
    {
        foreach (var ep in new[] { r.Source, r.Target })
            foreach (var a in g.Adjacency[ep]) if (a.Rel == r.Id && a.To is null) a.To = rev;
    }

    public IReadOnlyList<ReadResult> Read(GraphKey gk, long revision, IReadOnlyList<ElementKey> keys)
    {
        lock (gate)
        {
            var g = Get(gk);
            if (revision > g.Commits.Count) return [.. keys.Select(_ => (ReadResult)new ReadResult.Unavailable())];
            return [.. keys.Select(k =>
            {
                if (!g.Versions.TryGetValue(k, out var l)) return (ReadResult)new ReadResult.Absent();
                var v = l.FirstOrDefault(v => v.From <= revision && (v.To is null || revision < v.To));
                return v is null ? new ReadResult.Absent() : new ReadResult.Present(v.State, v.From, v.To);
            })];
        }
    }

    public AdjacencyPage ScanAdjacency(GraphKey gk, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation)
    {
        lock (gate)
        {
            var g = Get(gk);
            if (!g.Adjacency.TryGetValue(endpoint, out var l)) return new AdjacencyPage([], null);
            var all = l.Where(a => a.Dir == dir && a.From <= revision && (a.To is null || revision < a.To) && (type is null || a.Type == type))
                .OrderBy(a => a.Rel, StringComparer.Ordinal).Where(a => continuation is null || string.CompareOrdinal(a.Rel, continuation) > 0).Take(pageSize + 1).ToList();
            var page = all.Take(pageSize).Select(a => new AdjEntry(a.Rel, a.Other, a.Type, a.Dir)).ToList();
            return new AdjacencyPage(page, all.Count > pageSize ? page[^1].RelId : null);
        }
    }

    public IEnumerable<CommittedPatch> Follow(GraphKey gk, long after)
    {
        List<CommittedPatch> snapshot;
        lock (gate) { var g = Get(gk); snapshot = g.Commits.Skip((int)after).ToList(); }
        return snapshot;
    }

    Graph StateAt(G g, long rev)
    {
        var gr = new Graph();
        var edits = new List<Edit>();
        foreach (var (k, list) in g.Versions)
            if (list.FirstOrDefault(v => v.From <= rev && (v.To is null || rev < v.To)) is { } v) edits.Add(Edit.Create(v.State));
        var err = gr.Apply(edits);
        return err is null ? gr : throw new InvalidOperationException(err);
    }

    public SealInfo Seal(GraphKey gk, long revision)
    {
        lock (gate)
        {
            var g = Get(gk);
            var gr = StateAt(g, revision);
            return new SealInfo(revision, gr.Digest(), gr.Count, revision == 0 ? Frontier.Empty : g.Commits[(int)revision - 1].Frontier);
        }
    }

    public IEnumerable<Element> OpenSnapshot(GraphKey gk, long revision)
    {
        lock (gate) { return [.. StateAt(Get(gk), revision).Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)]; }
    }

    public IReadOnlyList<IdentityBinding> Bindings(GraphKey gk) { lock (gate) { return [.. Get(gk).Bindings]; } }
    public void Dispose() { }
}

/// <summary>SQLite implementation: the local schema of research 02 section 4.7. One BEGIN IMMEDIATE transaction per commit batch; open-interval uniqueness by partial unique indexes.</summary>
public sealed class SqliteGraphStore : IDurableStore
{
    readonly SqliteConnection db;
    readonly object gate = new();

    public SqliteGraphStore(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        db = new SqliteConnection(cs);
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS "commit"(t TEXT, g TEXT, rev INTEGER, frontier TEXT, cause TEXT, edits_json TEXT, PRIMARY KEY(t,g,rev));
            CREATE TABLE IF NOT EXISTS element_version(t TEXT, g TEXT, key TEXT, from_rev INTEGER, to_rev INTEGER, state_json TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_element_open ON element_version(t,g,key) WHERE to_rev IS NULL;
            CREATE INDEX IF NOT EXISTS ix_element ON element_version(t,g,key,from_rev);
            CREATE TABLE IF NOT EXISTS adjacency(t TEXT, g TEXT, endpoint TEXT, dir INTEGER, rel_id TEXT, from_rev INTEGER, to_rev INTEGER, type TEXT, other TEXT);
            CREATE INDEX IF NOT EXISTS ix_adj ON adjacency(t,g,endpoint,dir,rel_id);
            CREATE TABLE IF NOT EXISTS rel_unique(t TEXT, g TEXT, src TEXT, tgt TEXT, type TEXT, from_rev INTEGER, to_rev INTEGER, rel_id TEXT);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_rel_open ON rel_unique(t,g,src,tgt,type) WHERE to_rev IS NULL;
            CREATE TABLE IF NOT EXISTS journal(n INTEGER PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS seal(position INTEGER PRIMARY KEY, meta TEXT NOT NULL, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS identity_binding(t TEXT, domain TEXT, key_hash TEXT, rev INTEGER, entity TEXT, components_json TEXT, PRIMARY KEY(t,domain,key_hash));
            """);
    }

    void Exec(string sql, params (string, object?)[] ps) { using var c = db.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value); c.ExecuteNonQuery(); }
    T? Scalar<T>(string sql, params (string, object?)[] ps) { using var c = db.CreateCommand(); c.CommandText = sql; foreach (var (n, v) in ps) c.Parameters.AddWithValue(n, v ?? DBNull.Value); var r = c.ExecuteScalar(); return r is null or DBNull ? default : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture); }

    public long JournalCount { get { lock (gate) return Scalar<long?>("SELECT MAX(n) FROM journal") ?? 0; } }
    public void JournalAppend(string body) { lock (gate) Exec("INSERT INTO journal(n, body) VALUES((SELECT COALESCE(MAX(n), 0) + 1 FROM journal), $b)", ("$b", body)); }
    public IEnumerable<string> JournalRead(long after = 0)
    {
        lock (gate)
        {
            var list = new List<string>();
            using var c = db.CreateCommand(); c.CommandText = "SELECT body FROM journal WHERE n > $a ORDER BY n"; c.Parameters.AddWithValue("$a", after);
            using var r = c.ExecuteReader(); while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void SealPut(long position, string meta, byte[] data)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            using (var c = db.CreateCommand()) { c.Transaction = tx; c.CommandText = "INSERT OR REPLACE INTO seal(position, meta, data) VALUES($p, $m, $d)"; c.Parameters.AddWithValue("$p", position); c.Parameters.AddWithValue("$m", meta); c.Parameters.AddWithValue("$d", data); c.ExecuteNonQuery(); }
            using (var c = db.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM seal WHERE position NOT IN (SELECT position FROM seal ORDER BY position DESC LIMIT 2)"; c.ExecuteNonQuery(); }
            tx.Commit();
        }
    }

    public SealRecord? SealLatest()
    {
        lock (gate)
        {
            using var c = db.CreateCommand(); c.CommandText = "SELECT position, meta, data FROM seal ORDER BY position DESC LIMIT 1";
            using var r = c.ExecuteReader();
            return r.Read() ? new SealRecord(r.GetInt64(0), r.GetString(1), (byte[])r[2]) : null;
        }
    }

    long HeadOf(GraphKey g) => Scalar<long?>("SELECT MAX(rev) FROM \"commit\" WHERE t=$t AND g=$g", ("$t", g.Tenant), ("$g", g.GraphId)) ?? 0;
    public HeadInfo Head(GraphKey g) { lock (gate) { var h = HeadOf(g); return new HeadInfo(h, h); } }

    Element? Current(GraphKey g, ElementKey k)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT state_json FROM element_version WHERE t=$t AND g=$g AND key=$k AND to_rev IS NULL";
        c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.GraphId); c.Parameters.AddWithValue("$k", k.ToString());
        var r = c.ExecuteScalar();
        return r is string s ? ElementJson.Decode(s) : null;
    }

    public CommitResult Commit(CommitBatch b)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction(deferred: false);
            try
            {
                var head = HeadOf(b.Graph);
                if (head != b.ExpectedHead) { tx.Rollback(); return new CommitResult.Conflict(head); }
                for (var i = 0; i < b.Patches.Count; i++)
                {
                    var p = b.Patches[i]; var rev = head + i + 1;
                    var edits = p.Edits.Where(e => e.Effective).ToList();
                    if (p.From != head + i || p.To != rev || edits.Count == 0) { tx.Rollback(); return new CommitResult.Rejected($"patch {i} has revisions {p.From}->{p.To}, expected {head + i}->{rev}, or is empty"); }
                    foreach (var e in edits)
                    {
                        if (!e.WellKeyed) { tx.Rollback(); return new CommitResult.Rejected($"edit {e.Key}: state is not under its own key"); }
                        var cur = Current(b.Graph, e.Key);
                        if (!Equals(cur, e.Before)) { tx.Rollback(); return new CommitResult.Rejected($"edit {e.Key}: guard failed, current state differs from before-state"); }
                    }
                    foreach (var e in edits.Where(e => e.Before is not null))
                    {
                        Exec("UPDATE element_version SET to_rev=$r WHERE t=$t AND g=$g AND key=$k AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$k", e.Key.ToString()));
                        if (e.Before is Rel br)
                        {
                            Exec("UPDATE adjacency SET to_rev=$r WHERE t=$t AND g=$g AND rel_id=$id AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$id", br.Id));
                            Exec("UPDATE rel_unique SET to_rev=$r WHERE t=$t AND g=$g AND rel_id=$id AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$id", br.Id));
                        }
                    }
                    foreach (var e in edits.Where(e => e.After is not null))
                    {
                        try
                        {
                            Exec("INSERT INTO element_version VALUES($t,$g,$k,$r,NULL,$s)", ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$k", e.Key.ToString()), ("$r", rev), ("$s", ElementJson.Encode(e.After!)));
                            if (e.After is Rel ar)
                            {
                                Exec("INSERT INTO rel_unique VALUES($t,$g,$s,$x,$ty,$r,NULL,$id)", ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$s", ar.Source), ("$x", ar.Target), ("$ty", ar.Type), ("$r", rev), ("$id", ar.Id));
                                Exec("INSERT INTO adjacency VALUES($t,$g,$e,0,$id,$r,NULL,$ty,$o)", ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$e", ar.Source), ("$id", ar.Id), ("$r", rev), ("$ty", ar.Type), ("$o", ar.Target));
                                Exec("INSERT INTO adjacency VALUES($t,$g,$e,1,$id,$r,NULL,$ty,$o)", ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$e", ar.Target), ("$id", ar.Id), ("$r", rev), ("$ty", ar.Type), ("$o", ar.Source));
                            }
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                        {
                            tx.Rollback();
                            return new CommitResult.Rejected($"edit {e.Key}: uniqueness violated (U): {ex.Message}");
                        }
                    }
                    Exec("INSERT INTO \"commit\" VALUES($t,$g,$r,$f,$c,$e)", ("$t", b.Graph.Tenant), ("$g", b.Graph.GraphId), ("$r", rev), ("$f", ElementJson.EncodeFrontier(b.Frontiers[i])), ("$c", string.Join("\n", p.Cause.Inputs)), ("$e", ElementJson.EncodeEdits(edits)));
                }
                foreach (var bd in b.Bindings)
                    Exec("INSERT OR IGNORE INTO identity_binding VALUES($t,$d,$k,$r,$e,$c)", ("$t", b.Graph.Tenant), ("$d", bd.Domain), ("$k", bd.KeyHash), ("$r", head + b.Patches.Count), ("$e", bd.Entity), ("$c", bd.ComponentsJson));
                tx.Commit();
                return new CommitResult.Committed(head + b.Patches.Count);
            }
            catch { try { tx.Rollback(); } catch { /* already rolled back */ } throw; }
        }
    }

    public IReadOnlyList<ReadResult> Read(GraphKey g, long revision, IReadOnlyList<ElementKey> keys)
    {
        lock (gate)
        {
            if (revision > HeadOf(g)) return [.. keys.Select(_ => (ReadResult)new ReadResult.Unavailable())];
            var res = new List<ReadResult>();
            foreach (var k in keys)
            {
                using var c = db.CreateCommand();
                c.CommandText = "SELECT state_json, from_rev, to_rev FROM element_version WHERE t=$t AND g=$g AND key=$k AND from_rev<=$r AND (to_rev IS NULL OR $r<to_rev)";
                c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.GraphId); c.Parameters.AddWithValue("$k", k.ToString()); c.Parameters.AddWithValue("$r", revision);
                using var r = c.ExecuteReader();
                res.Add(r.Read() ? new ReadResult.Present(ElementJson.Decode(r.GetString(0)), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2)) : new ReadResult.Absent());
            }
            return res;
        }
    }

    public AdjacencyPage ScanAdjacency(GraphKey g, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation)
    {
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT rel_id, other, type FROM adjacency WHERE t=$t AND g=$g AND endpoint=$e AND dir=$d AND from_rev<=$r AND (to_rev IS NULL OR $r<to_rev) AND ($ty IS NULL OR type=$ty) AND ($c IS NULL OR rel_id>$c) ORDER BY rel_id COLLATE BINARY LIMIT $n";
            c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.GraphId); c.Parameters.AddWithValue("$e", endpoint); c.Parameters.AddWithValue("$d", dir == Direction.Out ? 0 : 1);
            c.Parameters.AddWithValue("$r", revision); c.Parameters.AddWithValue("$ty", (object?)type ?? DBNull.Value); c.Parameters.AddWithValue("$c", (object?)continuation ?? DBNull.Value); c.Parameters.AddWithValue("$n", pageSize + 1);
            var all = new List<AdjEntry>();
            using (var r = c.ExecuteReader()) while (r.Read()) all.Add(new AdjEntry(r.GetString(0), r.GetString(1), r.GetString(2), dir));
            var page = all.Take(pageSize).ToList();
            return new AdjacencyPage(page, all.Count > pageSize ? page[^1].RelId : null);
        }
    }

    public IEnumerable<CommittedPatch> Follow(GraphKey g, long after)
    {
        var res = new List<CommittedPatch>();
        lock (gate)
        {
            using var c = db.CreateCommand();
            c.CommandText = "SELECT rev, frontier, cause, edits_json FROM \"commit\" WHERE t=$t AND g=$g AND rev>$a ORDER BY rev";
            c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.GraphId); c.Parameters.AddWithValue("$a", after);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var rev = r.GetInt64(0);
                var cause = new Cause([.. r.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries)], "0");
                res.Add(new CommittedPatch(new Patch(g.GraphId, rev - 1, rev, ElementJson.DecodeEdits(r.GetString(3)), cause), ElementJson.DecodeFrontier(r.GetString(1))));
            }
        }
        return res;
    }

    Graph StateAt(GraphKey g, long rev)
    {
        var edits = new List<Edit>();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT state_json FROM element_version WHERE t=$t AND g=$g AND from_rev<=$r AND (to_rev IS NULL OR $r<to_rev)";
        c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.GraphId); c.Parameters.AddWithValue("$r", rev);
        using (var r = c.ExecuteReader()) while (r.Read()) edits.Add(Edit.Create(ElementJson.Decode(r.GetString(0))));
        var gr = new Graph();
        var err = gr.Apply(edits);
        return err is null ? gr : throw new InvalidOperationException(err);
    }

    public SealInfo Seal(GraphKey g, long revision)
    {
        lock (gate)
        {
            var gr = StateAt(g, revision);
            var fr = revision == 0 ? Frontier.Empty : ElementJson.DecodeFrontier(Scalar<string>("SELECT frontier FROM \"commit\" WHERE t=$t AND g=$g AND rev=$r", ("$t", g.Tenant), ("$g", g.GraphId), ("$r", revision))!);
            return new SealInfo(revision, gr.Digest(), gr.Count, fr);
        }
    }

    public IEnumerable<Element> OpenSnapshot(GraphKey g, long revision) { lock (gate) { return [.. StateAt(g, revision).Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)]; } }

    public long BindingCount(string tenant) => Scalar<long?>("SELECT COUNT(*) FROM identity_binding WHERE t=$t", ("$t", tenant)) ?? 0;
    public void Dispose() { db.Dispose(); }
}

// ---- telemetry port --------------------------------------------------------------------------------------

/// <summary>One telemetry sample or closed-window aggregate: element and property handles are interned by the engine. Deleted samples retract a window (the engine's window retention and revision, an extension of Append).</summary>
/// <summary>`AuxA`, `AuxB`: the pair a window of a `mean` or `last` property carries next to its value (Law 7.5 item 3 (i)): for `mean` the sum and the count it is the quotient of, for `last` the order value of the row it took (B unused). Null for every other window.</summary>
public readonly record struct Sample(int Element, int Prop, long WindowStart, long WidthNs, decimal Value, long InputId, bool Deleted = false, decimal? AuxA = null, decimal? AuxB = null);
public readonly record struct WindowKey(int Element, int Prop, long WindowStart);
public readonly record struct WindowChange(WindowKey Key, decimal? Old, decimal? New);
public readonly record struct SeriesPoint(long WindowStart, decimal Value);
public delegate void WindowVisitor(int element, int prop, long windowStart, decimal value);
public sealed record SeriesRead(int Element, int Prop, IReadOnlyList<SeriesPoint> Points, string Completeness);

/// <summary>The three-operation telemetry port of research 02 section 3.2: Append, ReadWindow, ClosedWindows. No revisions, no commit promise.</summary>
public interface ITelemetryStore
{
    /// <summary>At-least-once: a sample with the value its window has already is no change (a duplicate delivery, or a rewrite with the same value); another value replaces it. Returns the position after the batch.</summary>
    long Append(GraphKey g, ReadOnlySpan<Sample> samples);
    /// <summary>`Append` of a sample whose window the store does not hold; false, and no change, when it holds the window. A flush learns that a window is new and writes it with one lookup.</summary>
    bool AppendNew(GraphKey g, in Sample s);
    IReadOnlyList<SeriesRead> ReadWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs);
    /// <summary>The same windows without allocating: each window of each (element, property) series in `[fromNs, toNs)` is handed to the visitor, in series order and window order. Returns the number of windows visited. A view of 10,000 elements over an hour is 2.4 M windows; reading them into lists allocated 148 MB and the collections that followed were the 300 ms outliers of H8.</summary>
    long ScanWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs, WindowVisitor visit);
    /// <summary>Every window of a graph in `[fromNs, toNs)`, whatever element it belongs to (one that left the graph too): each is handed to the visitor, the windows of a series in window order, the series in no stated order. Returns the number of windows. Without a visitor it only counts, and allocates nothing.</summary>
    long ScanGraph(GraphKey g, WindowVisitor? visit, long fromNs = long.MinValue, long toNs = long.MaxValue);
    /// <summary>The (element, property) series the graph's plane holds.</summary>
    int SeriesCount(GraphKey g);
    /// <summary>The windows marked partial (`MarkPartial`), by window start: the reason and the number of lost events.</summary>
    IReadOnlyDictionary<long, (string Reason, long Count)> PartialWindows(GraphKey g);
    /// <summary>Windows that a merge left without a value (`MoveElement`), counted once each.</summary>
    long MergeConflicts(GraphKey g);
    /// <summary>Window changes after a position, in order. Each change is emitted once; this stream is the only telemetry an engine input may carry (L12). A plane has one reader, its graph, which reads right after its step: what is returned is released.</summary>
    IReadOnlyList<WindowChange> ClosedWindows(GraphKey g, long afterPosition, out long newPosition);
    /// <summary>A source reported lost events (a loss mark): the window becomes Partial(sourceLoss) (Def. 7.7). Part of the ingest side of the port.</summary>
    void MarkPartial(GraphKey g, long windowStart, string reason, long count);
    /// <summary>An element changed its id (two identities merged): its windows move to the new id. Where the new id already has the window, the two values combine as if every row had been written under the survivor from the start (Law 7.5): by the property's combine rule for `sum`, `min` and `max` (`combineOf` gives the rule of a property); under any other rule equal values are one value and different values are a conflict, so the window has no value. Each moved window is a window change (removed under the old id, set or removed under the new one) so derived graphs follow.</summary>
    void MoveElement(GraphKey g, int from, int to, Func<int, string?>? combineOf = null);
    /// <summary>The value of one window, if the store has it.</summary>
    decimal? ValueOf(GraphKey g, WindowKey key);
    /// <summary>The pair a window carries next to its value (`Sample.AuxA`, `AuxB`), if any.</summary>
    (decimal A, decimal B)? AuxOf(GraphKey g, WindowKey key);
    /// <summary>Retention (Def. 7.13): a closed window [a, b) is kept while b is after the logical frontier minus the declared age. A window that is dropped here is not a retraction: no change is recorded, and what was derived from it stays.</summary>
    void Retain(GraphKey g, long frontierNs, long ageNs);
    /// <summary>The instant before which the windows of the graph are no longer kept; null before the first advance.</summary>
    long? RetainedFrom(GraphKey g);
}

public sealed class InMemoryTelemetryStore(int logCapacity = 4_000_000) : ITelemetryStore
{
    /// <summary>
    /// The windows of one series in start order, the first `Count` slots used, and the width of its windows (to know where they end). Compact: a
    /// start is an int number of widths from the first start the series had, a value its mantissa and scale in one long. A start off that grid
    /// or too far from it turns the series' starts to plain longs, a value whose mantissa needs more than 58 bits (or a negative zero) its values
    /// to plain decimals, so every start and value reads back exactly as written.
    /// </summary>
    sealed class Windows(long width)
    {
        public readonly long Width = width;
        public int Count { get; private set; }
        long origin; int[] steps = []; long[]? starts;      // `starts`: the plain form, once a start is off the grid
        long[] packed = []; decimal[]? values;               // `values`: the plain form, once a value does not pack
        public long Start(int i) => starts is null ? origin + (long)steps[i] * Width : starts[i];
        public decimal Value(int i) => values is null ? Unpack(packed[i]) : values[i];
        public void SetValue(int i, decimal v)
        {
            if (values is null && TryPack(v, out var code)) { packed[i] = code; return; }
            PlainValues(); values![i] = v;
        }
        /// <summary>The slot of a window start, or the complement of the slot it would take.</summary>
        public int Find(long start)
        {
            int lo = 0, hi = Count - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1; var at = Start(mid);
                if (at == start) return mid;
                if (at < start) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }
        /// <summary>The first slot whose window starts at or after an instant.</summary>
        public int From(long start) { var i = Find(start); return i < 0 ? ~i : i; }
        public void Insert(int i, long start, decimal value)
        {
            if (Count == 0 && starts is null) origin = start;
            var d = start - origin;
            var onGrid = starts is null && d % Width == 0 && d / Width is >= int.MinValue and <= int.MaxValue;
            if (starts is null && !onGrid) PlainStarts();
            if (Count == Capacity) Grow();
            if (starts is null) { Array.Copy(steps, i, steps, i + 1, Count - i); steps[i] = (int)(d / Width); }
            else { Array.Copy(starts, i, starts, i + 1, Count - i); starts[i] = start; }
            if (values is null) Array.Copy(packed, i, packed, i + 1, Count - i); else Array.Copy(values, i, values, i + 1, Count - i);
            Count++;
            SetValue(i, value);
        }
        public void Set(long start, decimal value) { var i = Find(start); if (i >= 0) SetValue(i, value); else Insert(~i, start, value); }
        /// <summary>Removes `n` slots from slot `i`.</summary>
        public void RemoveAt(int i, int n = 1)
        {
            if (starts is null) Array.Copy(steps, i + n, steps, i, Count - i - n); else Array.Copy(starts, i + n, starts, i, Count - i - n);
            if (values is null) Array.Copy(packed, i + n, packed, i, Count - i - n); else Array.Copy(values, i + n, values, i, Count - i - n);
            Count -= n;
        }

        int Capacity => starts?.Length ?? steps.Length;
        void Grow()
        {
            var cap = Count == 0 ? 4 : 2 * Count;
            if (starts is null) Array.Resize(ref steps, cap); else Array.Resize(ref starts, cap);
            if (values is null) Array.Resize(ref packed, cap); else Array.Resize(ref values, cap);
        }
        void PlainStarts()
        {
            var plain = new long[Capacity];
            for (var k = 0; k < Count; k++) plain[k] = Start(k);
            starts = plain; steps = [];
        }
        void PlainValues()
        {
            if (values is not null) return;
            var plain = new decimal[Capacity];
            for (var k = 0; k < Count; k++) plain[k] = Unpack(packed[k]);
            values = plain; packed = [];
        }
        // a value as its signed mantissa shifted by 5 and its scale (0 to 28) in the low 5 bits; the mantissa below 2^58
        static bool TryPack(decimal v, out long code)
        {
            Span<int> b = stackalloc int[4];
            decimal.GetBits(v, b);
            code = 0;
            if (b[2] != 0 || (uint)b[1] >= 1u << 26) return false;
            var m = (long)((ulong)(uint)b[1] << 32 | (uint)b[0]); var neg = b[3] < 0;
            if (neg && m == 0) return false;
            code = (neg ? -m : m) << 5 | (long)((b[3] >> 16) & 0xFF);
            return true;
        }
        static decimal Unpack(long code)
        {
            var m = code >> 5; var neg = m < 0; if (neg) m = -m;
            return new decimal((int)(uint)m, (int)(uint)(m >>> 32), 0, neg, (byte)(code & 31));
        }
    }

    sealed class Plane
    {
        public readonly Dictionary<(int, int), Windows> Series = [];
        /// <summary>The changes not yet read; a read takes the list. An append makes room for its whole batch at once, so a log is made at its size, not grown by doubling.</summary>
        public List<WindowChange> Log = [];
        public long Base; // position of Log[0]
        /// <summary>The changes the last read took: a log that starts to fill one window at a time is made at that size, so a steady close writes its log without growing it.</summary>
        public int LastRead;
        public long? RetainedFrom;
        /// <summary>Windows that a merge left without a value because their two values could not be combined (Law 7.5). "No value" is absorbing over merges: the window of a third id does not bring a value back. A later write of the window (the rows as they are aggregated now) ends it.</summary>
        public readonly HashSet<WindowKey> NoValue = [];
        /// <summary>The pairs of the windows that carry one (`mean`: sum and count, `last`: order); absent for every other window.</summary>
        public readonly Dictionary<WindowKey, (decimal A, decimal B)> Aux = [];
        public long MergeConflicts;
        public readonly Dictionary<long, (string Reason, long Count)> Partials = [];
    }
    readonly Dictionary<GraphKey, Plane> planes = [];
    readonly object gate = new();
    /// <summary>The plane last looked up: the writes of a flush and the reads of a step go to one plane in a row, so most lookups hash no text.</summary>
    GraphKey lastKey; Plane? last;
    Plane Get(GraphKey g)
    {
        if (last is not null && ReferenceEquals(lastKey.Tenant, g.Tenant) && ReferenceEquals(lastKey.GraphId, g.GraphId)) return last;
        if (!planes.TryGetValue(g, out var p)) planes[g] = p = new Plane();
        (lastKey, last) = (g, p);
        return p;
    }

    public long Append(GraphKey g, ReadOnlySpan<Sample> samples)
    {
        lock (gate)
        {
            var p = Get(g);
            p.Log.EnsureCapacity(p.Log.Count + samples.Length);
            foreach (var s in samples)
            {
                var key = (s.Element, s.Prop);
                var wk = new WindowKey(s.Element, s.Prop, s.WindowStart);
                if (s.Deleted) p.Aux.Remove(wk);
                else if (s.AuxA is { } aa) p.Aux[wk] = (aa, s.AuxB ?? 0); else p.Aux.Remove(wk);
                if (!p.Series.TryGetValue(key, out var ser))
                {
                    if (s.Deleted) continue;
                    p.Series[key] = ser = new Windows(s.WidthNs);
                }
                var at = ser.Find(s.WindowStart);
                var old = at >= 0 ? ser.Value(at) : 0;
                if (s.Deleted)
                {
                    if (at < 0) continue;
                    ser.RemoveAt(at);
                    if (ser.Count == 0) p.Series.Remove(key);
                    p.Log.Add(new WindowChange(wk, old, null));
                    continue;
                }
                p.NoValue.Remove(wk);
                if (at >= 0) { var same = old == s.Value; ser.SetValue(at, s.Value); if (same) continue; }      // the same value: a duplicate delivery or a rewrite, no change
                else ser.Insert(~at, s.WindowStart, s.Value);
                p.Log.Add(new WindowChange(wk, at >= 0 ? old : null, s.Value));
            }
            if (p.Log.Count > logCapacity) { var drop = p.Log.Count - logCapacity / 2; p.Log.RemoveRange(0, drop); p.Base += drop; }
            return p.Base + p.Log.Count;
        }
    }

    public bool AppendNew(GraphKey g, in Sample s)
    {
        lock (gate)
        {
            var p = Get(g);
            var key = (s.Element, s.Prop);
            if (!p.Series.TryGetValue(key, out var ser)) p.Series[key] = ser = new Windows(s.WidthNs);
            var at = ser.Find(s.WindowStart);
            if (at >= 0) return false;
            var wk = new WindowKey(s.Element, s.Prop, s.WindowStart);
            if (s.AuxA is { } aa) p.Aux[wk] = (aa, s.AuxB ?? 0);          // `Aux` holds only windows the plane holds: a new window has nothing there to remove
            p.NoValue.Remove(wk);
            ser.Insert(~at, s.WindowStart, s.Value);
            if (p.Log.Count == 0) p.Log.EnsureCapacity(p.LastRead);
            p.Log.Add(new WindowChange(wk, null, s.Value));
            if (p.Log.Count > logCapacity) { var drop = p.Log.Count - logCapacity / 2; p.Log.RemoveRange(0, drop); p.Base += drop; }
            return true;
        }
    }

    public long ScanWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs, WindowVisitor visit)
    {
        lock (gate)
        {
            var p = Get(g); long n = 0;
            foreach (var e in elements)
                foreach (var pr in props)
                {
                    if (!p.Series.TryGetValue((e, pr), out var ser)) continue;
                    for (var i = ser.From(fromNs); i < ser.Count && ser.Start(i) < toNs; i++) { visit(e, pr, ser.Start(i), ser.Value(i)); n++; }
                }
            return n;
        }
    }

    public IReadOnlyList<SeriesRead> ReadWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs)
    {
        lock (gate)
        {
            var p = Get(g);
            var res = new List<SeriesRead>();
            foreach (var e in elements)
                foreach (var pr in props)
                {
                    List<SeriesPoint> pts;
                    if (p.Series.TryGetValue((e, pr), out var ser))
                    {
                        int lo = ser.From(fromNs), end = Math.Max(lo, ser.From(toNs));
                        pts = new List<SeriesPoint>(end - lo);
                        for (var i = lo; i < end; i++) pts.Add(new SeriesPoint(ser.Start(i), ser.Value(i)));
                    }
                    else pts = [];
                    var partial = p.Partials.Count == 0 ? default : pts.Select(x => p.Partials.GetValueOrDefault(x.WindowStart)).FirstOrDefault(x => x.Reason is not null);
                    res.Add(new SeriesRead(e, pr, pts, partial.Reason is null ? "Complete" : $"Partial({partial.Reason})"));
                }
            return res;
        }
    }

    public IReadOnlyList<WindowChange> ClosedWindows(GraphKey g, long afterPosition, out long newPosition)
    {
        lock (gate)
        {
            var p = Get(g);
            if (afterPosition < p.Base) throw new GapException(p.Base);
            // A position says what its reader has consumed, and what it reads now it has consumed too: the plane's one reader is its graph, right after its step. So the log holds the changes not yet read, not the history of the plane.
            var consumed = (int)(afterPosition - p.Base);
            if (consumed > 0) p.Log.RemoveRange(0, consumed);
            newPosition = p.Base = afterPosition + p.Log.Count;
            if (p.Log.Count == 0) return [];
            var read = p.Log; p.Log = []; p.LastRead = read.Count;          // its reader keeps what it reads (the graph delta, the commit event, an export)
            return read;
        }
    }

    public decimal? ValueOf(GraphKey g, WindowKey key)
    {
        lock (gate) return Get(g).Series.TryGetValue((key.Element, key.Prop), out var ser) && ser.Find(key.WindowStart) is var i and >= 0 ? ser.Value(i) : null;
    }
    public (decimal A, decimal B)? AuxOf(GraphKey g, WindowKey key) { lock (gate) return Get(g).Aux.TryGetValue(key, out var a) ? a : null; }
    public int SeriesCount(GraphKey g) { lock (gate) return Get(g).Series.Count; }

    /// <summary>A count is the windows of each series between two binary searches (every series' count over the whole time line), not a walk over the windows.</summary>
    public long ScanGraph(GraphKey g, WindowVisitor? visit, long fromNs = long.MinValue, long toNs = long.MaxValue)
    {
        lock (gate)
        {
            if (!planes.TryGetValue(g, out var p)) return 0;
            long n = 0;
            foreach (var ((e, pr), ser) in p.Series)
            {
                int lo = fromNs == long.MinValue ? 0 : ser.From(fromNs), end = toNs == long.MaxValue ? ser.Count : Math.Max(lo, ser.From(toNs));
                if (visit is not null) for (var i = lo; i < end; i++) visit(e, pr, ser.Start(i), ser.Value(i));
                n += end - lo;
            }
            return n;
        }
    }

    /// <summary>Two windows of one property under two ids of a class, as value and optional pair: their combination as if every row had been written under one id (Law 7.5 item 3). `sum`, `min`, `max` in their monoid; `mean` with sum and count by adding both and dividing; `last` with its order by the greater order, a tie by the greater value; otherwise equal values are that value and different ones none.</summary>
    public static (decimal Value, (decimal A, decimal B)? Aux)? Combine(string? rule, decimal v1, (decimal A, decimal B)? a1, decimal v2, (decimal A, decimal B)? a2) => rule switch
    {
        "sum" => (v1 + v2, null),
        "min" => (Math.Min(v1, v2), null),
        "max" => (Math.Max(v1, v2), null),
        "mean" when a1 is { } p1 && a2 is { } p2 && p1.B + p2.B != 0 => ((p1.A + p2.A) / (p1.B + p2.B), (p1.A + p2.A, p1.B + p2.B)),
        "last" when a1 is { } o1 && a2 is { } o2 => o1.A > o2.A || o1.A == o2.A && v1 >= v2 ? (v1, a1) : (v2, a2),
        _ => v1 == v2 && a1 == a2 ? (v1, a1) : null,
    };

    public void MoveElement(GraphKey g, int from, int to, Func<int, string?>? combineOf = null)
    {
        lock (gate)
        {
            var p = Get(g);
            foreach (var key in p.Series.Keys.Where(k => k.Item1 == from).ToList())
            {
                var src = p.Series[key]; var dst = (to, key.Item2);
                if (!p.Series.TryGetValue(dst, out var target)) p.Series[dst] = target = new Windows(src.Width);
                for (var i = 0; i < src.Count; i++)
                {
                    var ws = src.Start(i); var value = src.Value(i);
                    p.Log.Add(new WindowChange(new WindowKey(from, key.Item2, ws), value, null));
                    var slot = target.Find(ws); var had = slot >= 0; var old = had ? target.Value(slot) : 0;
                    var at = new WindowKey(to, key.Item2, ws);
                    var fromKey = new WindowKey(from, key.Item2, ws);
                    (decimal A, decimal B)? auxFrom = p.Aux.TryGetValue(fromKey, out var af) ? af : null, auxTo = p.Aux.TryGetValue(at, out var at2) ? at2 : null;
                    p.Aux.Remove(fromKey);
                    // two values and no rule that makes one of them: a conflict, the window has no value
                    var combined = p.NoValue.Contains(at) ? null : !had ? (value, auxFrom) : Combine(combineOf?.Invoke(key.Item2), old, auxTo, value, auxFrom);
                    if (combined is { } c) { target.Set(ws, c.Value); if (c.Aux is { } ca) p.Aux[at] = ca; else p.Aux.Remove(at); }
                    else { if (had) target.RemoveAt(slot); p.Aux.Remove(at); if (p.NoValue.Add(at)) p.MergeConflicts++; }
                    var v = combined?.Value;
                    p.Log.Add(new WindowChange(new WindowKey(to, key.Item2, ws), had ? old : null, v));
                }
                p.Series.Remove(key);
                if (target.Count == 0) p.Series.Remove(dst);
            }
            // a window of the loser that was already without a value stays so under the survivor, and takes the survivor's value with it
            foreach (var mark in p.NoValue.Where(k => k.Element == from).ToList())
            {
                p.NoValue.Remove(mark);
                var at = mark with { Element = to };
                p.NoValue.Add(at);
                if (p.Series.TryGetValue((to, mark.Prop), out var target) && target.Find(mark.WindowStart) is >= 0 and var slot)
                {
                    var gone = target.Value(slot); target.RemoveAt(slot);
                    if (target.Count == 0) p.Series.Remove((to, mark.Prop));
                    p.Log.Add(new WindowChange(at, gone, null));
                }
            }
        }
    }

    public void Retain(GraphKey g, long frontierNs, long ageNs)
    {
        lock (gate)
        {
            var p = Get(g); var from = frontierNs - ageNs;
            p.RetainedFrom = from;
            foreach (var (key, ser) in p.Series)
            {
                var n = 0;
                while (n < ser.Count && ser.Start(n) + ser.Width <= from) { if (p.Aux.Count > 0) p.Aux.Remove(new WindowKey(key.Item1, key.Item2, ser.Start(n))); n++; }
                if (n > 0) ser.RemoveAt(0, n);
            }
            foreach (var key in p.Series.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) p.Series.Remove(key);
        }
    }

    public long? RetainedFrom(GraphKey g) { lock (gate) return Get(g).RetainedFrom; }
    public long MergeConflicts(GraphKey g) { lock (gate) return Get(g).MergeConflicts; }

    public void MarkPartial(GraphKey g, long windowStart, string reason, long count)
    {
        lock (gate) { var p = Get(g); var cur = p.Partials.GetValueOrDefault(windowStart); p.Partials[windowStart] = (cur.Reason is null || cur.Reason == reason ? reason : string.Join("+", cur.Reason.Split('+').Append(reason).Distinct().Order(StringComparer.Ordinal)), cur.Count + count); }
    }

    public IReadOnlyDictionary<long, (string Reason, long Count)> PartialWindows(GraphKey g) { lock (gate) { return new Dictionary<long, (string, long)>(Get(g).Partials); } }
}

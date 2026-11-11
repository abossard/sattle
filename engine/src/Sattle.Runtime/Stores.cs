using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sattle.Core;

namespace Sattle.Runtime;

public readonly record struct GraphKey(string Tenant, string Graph)
{
    public override string ToString() => Tenant + "/" + Graph;
}

public sealed record HeadInfo(long Head, long Watermark);
public sealed record IdentityBinding(string Domain, string KeyHash, string Entity, string ComponentsJson);
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

/// <summary>The six-operation storage port of research 02 section 3.2 (Head, Commit, Read, ScanAdjacency, Follow, Seal and OpenSnapshot). Synchronous in this slice: the in-memory and SQLite stores need no awaiting.</summary>
public interface IGraphStore : IDisposable
{
    HeadInfo Head(GraphKey g);
    CommitResult Commit(CommitBatch batch);
    IReadOnlyList<ReadResult> Read(GraphKey g, long revision, IReadOnlyList<ElementKey> keys);
    AdjacencyPage ScanAdjacency(GraphKey g, long revision, string endpoint, Direction dir, string? type, int pageSize, string? continuation);
    /// <summary>Committed patches after a revision, in order. Throws GapException when the store no longer has them.</summary>
    IEnumerable<CommittedPatch> Follow(GraphKey g, long after);
    SealInfo Seal(GraphKey g, long revision);
    IEnumerable<Element> OpenSnapshot(GraphKey g, long revision);
}

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
public sealed class InMemoryGraphStore : IGraphStore, IJournalStore
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
                g.Commits.Add(new CommittedPatch(new Patch(p.Graph, p.From, p.To, p.Edits.Where(e => e.Effective), p.Cause), b.Frontiers[i]));
            }
            g.Bindings.AddRange(b.Bindings);
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
public sealed class SqliteGraphStore : IGraphStore, IJournalStore
{
    readonly SqliteConnection db;
    readonly object gate = new();

    public SqliteGraphStore(string path)
    {
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

    long HeadOf(GraphKey g) => Scalar<long?>("SELECT MAX(rev) FROM \"commit\" WHERE t=$t AND g=$g", ("$t", g.Tenant), ("$g", g.Graph)) ?? 0;
    public HeadInfo Head(GraphKey g) { lock (gate) { var h = HeadOf(g); return new HeadInfo(h, h); } }

    Element? Current(GraphKey g, ElementKey k)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT state_json FROM element_version WHERE t=$t AND g=$g AND key=$k AND to_rev IS NULL";
        c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.Graph); c.Parameters.AddWithValue("$k", k.ToString());
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
                        Exec("UPDATE element_version SET to_rev=$r WHERE t=$t AND g=$g AND key=$k AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$k", e.Key.ToString()));
                        if (e.Before is Rel br)
                        {
                            Exec("UPDATE adjacency SET to_rev=$r WHERE t=$t AND g=$g AND rel_id=$id AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$id", br.Id));
                            Exec("UPDATE rel_unique SET to_rev=$r WHERE t=$t AND g=$g AND rel_id=$id AND to_rev IS NULL", ("$r", rev), ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$id", br.Id));
                        }
                    }
                    foreach (var e in edits.Where(e => e.After is not null))
                    {
                        try
                        {
                            Exec("INSERT INTO element_version VALUES($t,$g,$k,$r,NULL,$s)", ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$k", e.Key.ToString()), ("$r", rev), ("$s", ElementJson.Encode(e.After!)));
                            if (e.After is Rel ar)
                            {
                                Exec("INSERT INTO rel_unique VALUES($t,$g,$s,$x,$ty,$r,NULL,$id)", ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$s", ar.Source), ("$x", ar.Target), ("$ty", ar.Type), ("$r", rev), ("$id", ar.Id));
                                Exec("INSERT INTO adjacency VALUES($t,$g,$e,0,$id,$r,NULL,$ty,$o)", ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$e", ar.Source), ("$id", ar.Id), ("$r", rev), ("$ty", ar.Type), ("$o", ar.Target));
                                Exec("INSERT INTO adjacency VALUES($t,$g,$e,1,$id,$r,NULL,$ty,$o)", ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$e", ar.Target), ("$id", ar.Id), ("$r", rev), ("$ty", ar.Type), ("$o", ar.Source));
                            }
                        }
                        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                        {
                            tx.Rollback();
                            return new CommitResult.Rejected($"edit {e.Key}: uniqueness violated (U): {ex.Message}");
                        }
                    }
                    Exec("INSERT INTO \"commit\" VALUES($t,$g,$r,$f,$c,$e)", ("$t", b.Graph.Tenant), ("$g", b.Graph.Graph), ("$r", rev), ("$f", ElementJson.EncodeFrontier(b.Frontiers[i])), ("$c", string.Join("\n", p.Cause.Inputs)), ("$e", ElementJson.EncodeEdits(edits)));
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
                c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.Graph); c.Parameters.AddWithValue("$k", k.ToString()); c.Parameters.AddWithValue("$r", revision);
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
            c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.Graph); c.Parameters.AddWithValue("$e", endpoint); c.Parameters.AddWithValue("$d", dir == Direction.Out ? 0 : 1);
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
            c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.Graph); c.Parameters.AddWithValue("$a", after);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var rev = r.GetInt64(0);
                var cause = new Cause([.. r.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries)], "0");
                res.Add(new CommittedPatch(new Patch(g.Graph, rev - 1, rev, ElementJson.DecodeEdits(r.GetString(3)), cause), ElementJson.DecodeFrontier(r.GetString(1))));
            }
        }
        return res;
    }

    Graph StateAt(GraphKey g, long rev)
    {
        var edits = new List<Edit>();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT state_json FROM element_version WHERE t=$t AND g=$g AND from_rev<=$r AND (to_rev IS NULL OR $r<to_rev)";
        c.Parameters.AddWithValue("$t", g.Tenant); c.Parameters.AddWithValue("$g", g.Graph); c.Parameters.AddWithValue("$r", rev);
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
            var fr = revision == 0 ? Frontier.Empty : ElementJson.DecodeFrontier(Scalar<string>("SELECT frontier FROM \"commit\" WHERE t=$t AND g=$g AND rev=$r", ("$t", g.Tenant), ("$g", g.Graph), ("$r", revision))!);
            return new SealInfo(revision, gr.Digest(), gr.Count, fr);
        }
    }

    public IEnumerable<Element> OpenSnapshot(GraphKey g, long revision) { lock (gate) { return [.. StateAt(g, revision).Elements().OrderBy(e => e.Key.ToString(), StringComparer.Ordinal)]; } }

    public long BindingCount(string tenant) => Scalar<long?>("SELECT COUNT(*) FROM identity_binding WHERE t=$t", ("$t", tenant)) ?? 0;
    public void Dispose() { db.Dispose(); }
}

// ---- telemetry port --------------------------------------------------------------------------------------

/// <summary>One telemetry sample or closed-window aggregate: element and property handles are interned by the engine. Deleted samples retract a window (the engine's window retention and revision, an extension of Append).</summary>
public readonly record struct Sample(int Element, int Prop, long WindowStart, long WidthNs, decimal Value, long InputId, bool Deleted = false);
public readonly record struct WindowKey(int Element, int Prop, long WindowStart);
public sealed record WindowChange(WindowKey Key, decimal? Old, decimal? New);
public readonly record struct SeriesPoint(long WindowStart, decimal Value);
public delegate void WindowVisitor(int element, int prop, long windowStart, decimal value);
public sealed record SeriesRead(int Element, int Prop, IReadOnlyList<SeriesPoint> Points, string Completeness);

/// <summary>The three-operation telemetry port of research 02 section 3.2: Append, ReadWindow, ClosedWindows. No revisions, no commit promise.</summary>
public interface ITelemetryStore
{
    /// <summary>At-least-once: a sample with the same (element, property, window, input id) is removed as duplicate; a different input id for the same window replaces it. Returns the position after the batch.</summary>
    long Append(GraphKey g, ReadOnlySpan<Sample> samples);
    IReadOnlyList<SeriesRead> ReadWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs);
    /// <summary>The same windows without allocating: each window of each (element, property) series in `[fromNs, toNs)` is handed to the visitor, in series order and window order. Returns the number of windows visited. A view of 10,000 elements over an hour is 2.4 M windows; reading them into lists allocated 148 MB and the collections that followed were the 300 ms outliers of H8.</summary>
    long ScanWindow(GraphKey g, IReadOnlyList<int> elements, IReadOnlyList<int> props, long fromNs, long toNs, WindowVisitor visit);
    /// <summary>Window changes after a position, in order. Each change is emitted once; this stream is the only telemetry an engine input may carry (L12).</summary>
    IReadOnlyList<WindowChange> ClosedWindows(GraphKey g, long afterPosition, out long newPosition);
    /// <summary>A source reported lost events (a Hubble LostEvent): the window becomes Partial(sourceLoss) (Def. 7.7). Part of the ingest side of the port.</summary>
    void MarkPartial(GraphKey g, long windowStart, string reason, long count);
    /// <summary>An element changed its id (two identities merged): its windows move to the new id. Where the new id already has the window, the two values combine as if every row had been written under the survivor from the start (Law 7.5): by the property's combine rule for `sum`, `min` and `max` (`combineOf` gives the rule of a property); under any other rule equal values are one value and different values are a conflict, so the window has no value. Each moved window is a window change (removed under the old id, set or removed under the new one) so derived graphs follow.</summary>
    void MoveElement(GraphKey g, int from, int to, Func<int, string?>? combineOf = null);
    /// <summary>Retention (Def. 7.13): a closed window [a, b) is kept while b is after the logical frontier minus the declared age. A window that is dropped here is not a retraction: no change is recorded, and what was derived from it stays.</summary>
    void Retain(GraphKey g, long frontierNs, long ageNs);
    /// <summary>The instant before which the windows of the graph are no longer kept; null before the first advance.</summary>
    long? RetainedFrom(GraphKey g);
}

public sealed class InMemoryTelemetryStore(int logCapacity = 4_000_000) : ITelemetryStore
{
    sealed class Plane
    {
        public readonly Dictionary<(int, int), SortedList<long, (decimal Value, long Input)>> Series = [];
        public readonly List<WindowChange> Log = [];
        public long Base; // position of Log[0]
        public readonly Dictionary<(int, int), long> Width = [];     // the window width of each series, to know where its windows end
        public long? RetainedFrom;
        /// <summary>Windows that a merge left without a value because their two values could not be combined (Law 7.5). "No value" is absorbing over merges: the window of a third id does not bring a value back. A later write of the window (the rows as they are aggregated now) ends it.</summary>
        public readonly HashSet<WindowKey> NoValue = [];
        public long MergeConflicts;
        public readonly Dictionary<long, (string Reason, long Count)> Partials = [];
    }
    readonly Dictionary<GraphKey, Plane> planes = [];
    readonly object gate = new();
    Plane Get(GraphKey g) { if (!planes.TryGetValue(g, out var p)) planes[g] = p = new Plane(); return p; }

    public long Append(GraphKey g, ReadOnlySpan<Sample> samples)
    {
        lock (gate)
        {
            var p = Get(g);
            foreach (var s in samples)
            {
                var key = (s.Element, s.Prop);
                if (!p.Series.TryGetValue(key, out var ser)) { p.Series[key] = ser = []; p.Width[key] = s.WidthNs; }
                var had = ser.TryGetValue(s.WindowStart, out var old);
                if (s.Deleted)
                {
                    if (!had) continue;
                    ser.Remove(s.WindowStart);
                    p.Log.Add(new WindowChange(new WindowKey(s.Element, s.Prop, s.WindowStart), old.Value, null));
                    continue;
                }
                p.NoValue.Remove(new WindowKey(s.Element, s.Prop, s.WindowStart));
                if (had && old.Input == s.InputId && old.Value == s.Value) continue; // duplicate delivery
                if (had && old.Value == s.Value) { ser[s.WindowStart] = (s.Value, s.InputId); continue; }
                ser[s.WindowStart] = (s.Value, s.InputId);
                p.Log.Add(new WindowChange(new WindowKey(s.Element, s.Prop, s.WindowStart), had ? old.Value : null, s.Value));
            }
            if (p.Log.Count > logCapacity) { var drop = p.Log.Count - logCapacity / 2; p.Log.RemoveRange(0, drop); p.Base += drop; }
            return p.Base + p.Log.Count;
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
                    var keys = ser.Keys; var vals = ser.Values; int lo = 0, hi = keys.Count;
                    while (lo < hi) { var mid = (lo + hi) >> 1; if (keys[mid] < fromNs) lo = mid + 1; else hi = mid; }
                    for (var i = lo; i < keys.Count && keys[i] < toNs; i++) { visit(e, pr, keys[i], vals[i].Value); n++; }
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
                        var keys = ser.Keys; int lo = 0, hi = keys.Count;
                        while (lo < hi) { var mid = (lo + hi) >> 1; if (keys[mid] < fromNs) lo = mid + 1; else hi = mid; }   // first window at or after fromNs
                        int end = lo, top = keys.Count;
                        while (end < top) { var mid = (end + top) >> 1; if (keys[mid] < toNs) end = mid + 1; else top = mid; }   // first window at or after toNs
                        pts = new List<SeriesPoint>(end - lo);
                        for (var i = lo; i < end; i++) pts.Add(new SeriesPoint(keys[i], ser.Values[i].Value));
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
            newPosition = p.Base + p.Log.Count;
            // A position says what its reader has consumed. Nothing before it is read again, so it is released: the log holds the changes not yet read, not the history of the plane.
            var consumed = (int)(afterPosition - p.Base);
            if (consumed > 0) { p.Log.RemoveRange(0, consumed); p.Base = afterPosition; }
            return [.. p.Log];
        }
    }

    public void MoveElement(GraphKey g, int from, int to, Func<int, string?>? combineOf = null)
    {
        lock (gate)
        {
            var p = Get(g);
            foreach (var key in p.Series.Keys.Where(k => k.Item1 == from).ToList())
            {
                var src = p.Series[key]; var dst = (to, key.Item2);
                if (!p.Series.TryGetValue(dst, out var target)) { p.Series[dst] = target = []; p.Width[dst] = p.Width.GetValueOrDefault(key); }
                foreach (var (ws, (value, input)) in src.ToList())
                {
                    p.Log.Add(new WindowChange(new WindowKey(from, key.Item2, ws), value, null));
                    var had = target.TryGetValue(ws, out var old);
                    var at = new WindowKey(to, key.Item2, ws);
                    decimal? combined = p.NoValue.Contains(at) ? null : !had ? value : combineOf?.Invoke(key.Item2) switch
                    {
                        "sum" => old.Value + value,
                        "min" => Math.Min(old.Value, value),
                        "max" => Math.Max(old.Value, value),
                        _ => old.Value == value ? value : null,          // two values and no rule that makes one of them: a conflict, the window has no value
                    };
                    if (combined is { } v) target[ws] = (v, input);
                    else { target.Remove(ws); if (p.NoValue.Add(at)) p.MergeConflicts++; }
                    p.Log.Add(new WindowChange(new WindowKey(to, key.Item2, ws), had ? old.Value : null, combined));
                }
                p.Series.Remove(key); p.Width.Remove(key);
            }
            // a window of the loser that was already without a value stays so under the survivor, and takes the survivor's value with it
            foreach (var mark in p.NoValue.Where(k => k.Element == from).ToList())
            {
                p.NoValue.Remove(mark);
                var at = mark with { Element = to };
                p.NoValue.Add(at);
                if (p.Series.TryGetValue((to, mark.Prop), out var target) && target.TryGetValue(mark.WindowStart, out var gone)) { target.Remove(mark.WindowStart); p.Log.Add(new WindowChange(at, gone.Value, null)); }
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
                var width = p.Width.GetValueOrDefault(key);
                while (ser.Count > 0 && ser.Keys[0] + width <= from) ser.RemoveAt(0);
            }
            foreach (var key in p.Series.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) { p.Series.Remove(key); p.Width.Remove(key); }
        }
    }

    public long? RetainedFrom(GraphKey g) { lock (gate) return Get(g).RetainedFrom; }
    /// <summary>Windows that a merge left without a value (counted once each).</summary>
    public long MergeConflicts(GraphKey g) { lock (gate) return Get(g).MergeConflicts; }

    public void MarkPartial(GraphKey g, long windowStart, string reason, long count)
    {
        lock (gate) { var p = Get(g); var cur = p.Partials.GetValueOrDefault(windowStart); p.Partials[windowStart] = (cur.Reason is null || cur.Reason == reason ? reason : string.Join("+", cur.Reason.Split('+').Append(reason).Distinct().Order(StringComparer.Ordinal)), cur.Count + count); }
    }

    public IReadOnlyDictionary<long, (string Reason, long Count)> PartialWindows(GraphKey g) { lock (gate) { return new Dictionary<long, (string, long)>(Get(g).Partials); } }

    /// <summary>All current windows of a graph, ordered by (element, property, window). For snapshots and digests.</summary>
    public IReadOnlyList<(int Element, int Prop, long Ws, decimal Value)> Dump(GraphKey g)
    {
        lock (gate)
        {
            var p = Get(g);
            return [.. p.Series.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).SelectMany(kv => kv.Value.Select(w => (kv.Key.Item1, kv.Key.Item2, w.Key, w.Value.Value)))];
        }
    }
}

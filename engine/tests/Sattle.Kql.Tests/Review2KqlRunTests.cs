using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2, verifier v-kql: what a projection in KQL computes on a mini world (rows in, the ingestion projection written in KQL, the master out).
/// Each test fails on 4bbf5d4 for the reason its comment gives.</summary>
public class Review2KqlRunTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Summary { key: Text; bucket: Text; best: Integer; other?: Integer; note?: Text; }
        entity KA { key: Text; bucket: Text; }
        entity KB { key: Text; bucket: Text; }
        identity summary for Summary version "1" { key: Text by exact; }
        identity domA for KA version "1" { key: Text by exact; bucket: Text by exact; }
        identity domB for KB version "1" { key: Text by exact; bucket: Text by exact; }
        rows ItemRow { key: Text; bucket: Text; rank: Integer; x: Integer; y: Integer; note?: Text; }
        """;

    const string Space = """
        module t.space @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as b;
        space s {
          source items adapter "sattle.authoring" rows b.ItemRow;
          master graph master = b.ingest(items: items);
        }
        """;

    /// <summary>The mini world with its ingestion projection given in KQL: the front end's result, and the engine after the rows are ingested (null when the front end refused).</summary>
    static (KqlFiles Put, Engine? Engine) Run(string kql, params (string Key, string Bucket, long Rank, long X, long Y, string? Note)[] rows)
    {
        var put = KqlFiles.Of([new SourceFile("t.base.sattle", Base)], [new SourceFile("t.base.ingest.kql", kql)]);
        if (!put.Ok) return (put, null);
        var cat = new Catalog(new KqlSource());
        var res = cat.Load([.. put.Files, new SourceFile("t.space.sattle", Space)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        var e = new Engine(cat, cat.Spaces.Keys.Single());
        var rw = new RowWriter(e.RowLayout("items"), e.Interner);
        foreach (var (key, bucket, rank, x, y, note) in rows)
        {
            rw.Begin(1); rw.Text("key", key); rw.Text("bucket", bucket);
            rw.Int(rw.Col("rank"), rank); rw.Int(rw.Col("x"), x); rw.Int(rw.Col("y"), y);
            if (note is null) rw.Absent(rw.Col("note")); else rw.Text("note", note);
        }
        e.Ingest("items", rw.Build()!, 1);
        return (put, e);
    }

    static Value? P(Entity e, string prop) => e.Props.Get("d:t.base/prop:" + prop);

    /// <summary>Kusto returns the columns of one row of the group: a tie on the order gives (1, 9) or (2, 3). HEAD: one `last` per column, the pair (2, 9) is no input row.</summary>
    [Fact]
    public void Arg_max_gives_the_columns_of_one_row_when_two_rows_tie()
    {
        var (put, e) = Run("graph ingest : t.base;\ninput items : ItemRow;\n"
            + "nodes Summary on s with (rule = \"observed\") = items | summarize arg_max(rank, x, y) by bucket | project s = identity(\"summary\", bucket), bucket, key = bucket, best = x, other = y;\n",
            ("k1", "a", 1, 1, 9, null), ("k2", "a", 1, 2, 3, null));
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        using var _ = e;
        var a = Assert.Single(e!.Master.State.Entities);
        Assert.Contains((P(a, "best"), P(a, "other")), new (Value?, Value?)[] { (Value.Integer(1), Value.Integer(9)), (Value.Integer(2), Value.Integer(3)) });
    }

    /// <summary>Kusto: the row of the greatest x, whatever its other columns hold. HEAD: a row whose returned value is missing is skipped, the note of the older row is kept.</summary>
    [Fact]
    public void Arg_max_gives_the_value_of_the_greatest_row_also_when_that_row_has_none()
    {
        var (put, e) = Run("graph ingest : t.base;\ninput items : ItemRow;\n"
            + "nodes Summary on s with (rule = \"observed\") = items | summarize arg_max(x, note) by bucket | project s = identity(\"summary\", bucket), bucket, key = bucket, best = x, note;\n",
            ("k1", "a", 0, 1, 0, "older"), ("k2", "a", 0, 2, 0, null));
        Assert.True(put.Ok, string.Join("\n", put.Diagnostics));
        using var _ = e;
        var a = Assert.Single(e!.Master.State.Entities);
        Assert.Equal(Value.Integer(2), P(a, "best"));
        Assert.NotEqual(Value.Text("older"), P(a, "note"));
    }

    /// <summary>Two identity domains in one union column: refused, or the ids stay apart (Kusto's identity() is a different string per domain). HEAD: both branches are minted in domain A, 4 rows make 2 elements.</summary>
    [Fact]
    public void A_union_of_two_identity_domains_is_refused_or_keeps_the_ids_apart()
    {
        var (put, e) = Run("graph ingest : t.base;\ninput items : ItemRow;\n"
            + "nodes KA on g with (rule = \"observed\") = union (items | project g = identity(\"domA\", key, bucket), key, bucket), (items | project g = identity(\"domB\", key, bucket), key, bucket);\n",
            ("k1", "a", 0, 0, 0, null), ("k2", "b", 0, 0, 0, null));
        if (!put.Ok) return;
        using var _ = e;
        var ids = e!.Master.State.Entities.Select(x => x.Id).ToList();
        Assert.True(ids.Count == 4, $"{ids.Count} elements from 2 rows in 2 domains: " + string.Join(", ", ids));
    }
}

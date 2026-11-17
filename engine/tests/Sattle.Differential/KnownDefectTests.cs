using Sattle.Core;
using Sattle.Language;
using Sattle.Reference;
using Sattle.Runtime;
using static Sattle.Differential.Logs;

namespace Sattle.Differential;

/// <summary>The regression tests of code review defects that are not a comparison with the reference: an identity counter, the row writer, what a Match reads, a refusal of the checker. The comparisons are in `SmallestLogTests`.</summary>
public class KnownDefectTests
{
    static RowInput Row(string source, long weight, string k, string g, long n, long seconds, decimal? d = null) =>
        new(source, Logs.Row(("k", Value.Text(k)), ("g", Value.Text(g)), ("n", Value.Integer(n)), ("d", d is { } q ? Value.Decimal(q) : Value.Null), ("t", Value.Instant(Origin + seconds * Second))), weight);

    /// <summary>Review: Identity.cs:165 with Exprs.cs:107. An identity is allocated for the rows a sink emits (Def. 5.9), not for rows a Filter rejected.</summary>
    [Fact]
    public void A_row_that_a_filter_rejected_mints_no_identity()
    {
        var w = Worlds.FilteredEmit;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        EngineRun.Ingest(engine, [Row("a", 1, "k0", "g0", 0, 0), Row("a", 1, "k1", "g0", 1, 0), Row("a", 1, "k2", "g0", 5, 0)], 1);
        Assert.Single(engine.Master.State.Entities);
        Assert.Equal(1, engine.Identities.Minted);
    }

    /// <summary>Review: Operators.cs:58, Columnar.cs:59 (Synthetic.cs:22, Bench.cs:75). A column that a row writer never sets is absent. The row builder marks every column present, so it reads as the value with code 0.</summary>
    [Fact]
    public void A_column_that_the_writer_never_sets_is_absent()
    {
        var w = Worlds.OptionalProperty;
        using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
        var writer = new RowWriter(engine.RowLayout("a"), engine.Interner);
        writer.Begin(1); writer.Text("k", "k0"); writer.Text("g", "g0"); writer.Int(writer.Col("n"), 1); writer.Instant("t", Origin);       // d is not set
        engine.Ingest("a", writer.Build()!, 1);
        Assert.DoesNotContain(EngineRun.FactsOf(engine).Graphs["master"].Lines(), l => l.Contains("prop:d="));
    }

    /// <summary>Review: Catalog.cs:170 with Checker.cs:54, :638. A Match reads the properties its own projection uses. One checker serves every projection of a module and keeps its demand set, so a Match also reads what a same-named operator of an earlier projection demanded.</summary>
    [Fact]
    public void A_match_reads_only_the_properties_its_own_projection_uses()
    {
        const string Copies = """graph("master") | graph-match (i) where labels(i) has "Item" project """;
        var catalog = EngineRun.Load(
        [
            new SourceFile("w.base.sattle", Worlds.BaseModule),
            new SourceFile("w.two.sattle", """
                module w.two @ 1.0.0;
                import w.base @ 1.0.0 sha256:… as base;
                entity Copy { k: Text; g?: Text; }
                identity copy for Copy version "1" { k: Text by exact; }
                """),
            Worlds.Kql("w.two", "first", $"graph first : w.two;\ninput master : base;\nnodes Copy on c = {Copies}k = i.k, g = i.g | project c = identity(\"copy\", k), k, g;\n"),
            Worlds.Kql("w.two", "second", $"graph second : w.two;\ninput master : base;\nnodes Copy on c = {Copies}k = i.k | project c = identity(\"copy\", k), k;\n"),
        ]);
        var second = catalog.Projections.Values.Single(p => p.Name == "second").Ops.OfType<TMatch>().Single();
        Assert.Equal(["k"], second.Reads.Select(r => Ids.Name(r.PropId)));
    }

    /// <summary>Code review 2: a view names its elements by the element. A key in a domain that an equivalence policy joins is refused: after a link the row that leaves the view would compute the survivor's id and not find the element it made. The world `a view named by the element of a linked domain` shows the accepted form following the merge.</summary>
    [Fact]
    public void A_view_that_names_its_elements_by_a_key_of_a_linked_domain_is_refused()
    {
        var w = Worlds.LinkedWith("a view named by a key of a linked domain", "Link, EmitEntity in a view", "entity Tag { name?: Text; }",
            """nodes Tag on t = graph("master") | graph-match (p) where labels(p) has "Pod" project p = p.id, name = p.name | project t = identity("podName", name), name;""");
        var (code, message) = Assert.Single(new Catalog(EngineRun.Projections).Load([.. w.Files.Where(f => f.Path != "w.space.sattle")], updatePins: true).Diagnostics.Where(d => d.IsError).Select(d => (d.Code, d.Message)).Distinct());     // without the space, which only says that the view did not load
        Assert.Equal("IdentityLinked", code);
        Assert.Contains("keep its id (p.id)", message);
    }

    /// <summary>#112: in this world the numbers the texts get depend on how the log is cut (one row per batch against the largest batches), so the differential comparison of the world would see an output that depends on them.</summary>
    [Fact]
    public void Code_numbers_depend_on_the_cut_where_a_where_drops_rows()
    {
        var w = Worlds.TraverseAfterWhere;
        Dictionary<string, int> Codes(IReadOnlyList<Input> log, bool oneByOne)
        {
            using var engine = new Engine(w.Catalog, w.Catalog.Spaces.Keys.Single());
            long seq = 0; var batch = new List<RowInput>();
            void Flush() { if (batch.Count > 0) EngineRun.Ingest(engine, [.. batch], ++seq); batch.Clear(); }
            foreach (var row in log.Cast<RowInput>()) { if (oneByOne || (batch.Count > 0 && batch[0].Source != row.Source)) Flush(); batch.Add(row); }
            Flush();
            return engine.Interner.Slots.Select((t, i) => (t, i)).Where(x => x.t is not null).ToDictionary(x => x.t!, x => x.i);
        }
        var differ = Enumerable.Range(0, 12).Select(seed => w.Log(seed, 14)).Count(log => !Codes(log, true).OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(Codes(log, false).OrderBy(x => x.Key, StringComparer.Ordinal)));
        Assert.True(differ > 0, "no default seed gives the texts other numbers under another cut");
    }
}

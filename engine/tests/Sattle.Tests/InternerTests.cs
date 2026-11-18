using Sattle.Cli;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>#41: the texts of rows that a step drops die with the step. A text an adapter row or a stateless step brings in is young; it becomes old only when it reaches state.</summary>
public class InternerTests
{
    const string Base = """
        module t.base @ 1.0.0;
        entity Event { id: Text; tag: Text; }
        identity event for Event version "1" { id: Text by exact; }
        rows EventRow { id: Text; kind: Text; }
        """;

    const string Ingest = """
        graph ingest : t.base;
        input events : EventRow;
        nodes Event on ev with (rule = "observed") = events | project id, kind, tag = strcat(id, "!") | where kind == "kept" | project ev = identity("event", id), id, tag;
        """;

    const string Space = """
        module t.space @ 1.0.0;
        import t.base @ 1.0.0 sha256:… as base;
        space s {
          source events adapter "sattle.authoring" rows base.EventRow;
          master graph master = base.ingest(events: events);
        }
        """;

    static Engine Start()
    {
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load([new SourceFile("t.base.sattle", Base), new SourceFile("t.base.ingest.kql", Ingest), new SourceFile("t.space.sattle", Space)], updatePins: true);
        Assert.Empty(res.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
        return new Engine(cat, cat.Spaces.Keys.Single());
    }

    /// <summary>Rows 0, 3, 6, ... are kept; with `dropped` the others come too, each with an id and a computed tag no other row has.</summary>
    static void Feed(Engine e, int rows, int chunk, bool dropped)
    {
        var w = new RowWriter(e.RowLayout("events"), e.Interner); long seq = 0;
        for (var i = 0; i < rows; i++)
        {
            var kept = i % 3 == 0;
            if (!kept && !dropped) continue;
            w.Begin(); w.Text("id", "event-" + i); w.Text("kind", kept ? "kept" : "noise");
            if (w.Rows == chunk) e.Ingest("events", w.Build()!, ++seq);
        }
        if (w.Rows > 0) e.Ingest("events", w.Build()!, ++seq);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void Rows_a_where_drops_leave_the_live_texts_unchanged(int chunk)
    {
        using var kept = Start(); using var all = Start();
        Feed(kept, 300, chunk, dropped: false);
        Feed(all, 300, chunk, dropped: true);
        Assert.Equal(100, all.Master.State.EntityCount);
        Assert.Equal(kept.StateDigest(), all.StateDigest());
        Assert.Equal(kept.Interner.Live, all.Interner.Live);
    }

    /// <summary>#112: a row writer that a failure left with rows does not keep the interner from freeing once it is disposed (the lens writers are `using`).</summary>
    [Fact]
    public void A_writer_left_behind_by_a_failure_releases_the_interner_when_disposed()
    {
        var it = new Interner();
        using (var w = new RowWriter(new Layout(["id"], [false], [true]), it))
        {
            w.Begin(); w.Text("id", "event-1");
            Assert.True(it.Holding);
            Assert.Throws<KeyNotFoundException>(() => w.Text("no such column", "x"));
        }
        Assert.False(it.Holding);
    }

    /// <summary>#112: a step that throws inside a Map leaves the interner minting old texts, so a later stateful step cannot mint a young text into state.</summary>
    [Fact]
    public void A_step_that_throws_in_a_stateless_operator_leaves_no_young_minting_behind()
    {
        using var e = Start();
        Assert.ThrowsAny<Exception>(() => e.Ingest("events", new Batch(new Layout([], []), 1) { N = 1 }, 1));         // the Map reads columns the batch does not have
        Assert.False(e.Interner.MintYoung);
    }

    /// <summary>#112: the columns escaped before a stateful step are derived from the column types: every type that is not a number holds codes of the text table. A type added later is escaped unless it is listed here as a number.</summary>
    public static TheoryData<Tag> Tags => [.. Enum.GetValues<Tag>()];
    [Theory, MemberData(nameof(Tags))]
    public void Every_column_type_that_is_not_a_number_is_escaped(Tag tag)
    {
        var number = tag is Tag.Integer or Tag.Decimal or Tag.Boolean or Tag.Instant;
        var it = new Interner();
        var w = new RowWriter(new Layout(["c"], [false]), it);
        w.Begin(); w.Text(0, "young");
        var rows = w.Build()!;
        var typed = rows.WithColumns(Layout.Of(new Schema([new Col("c", new FType(new Ty(tag), true, Role.Data))])), rows.Cols);
        it.Escape(typed);
        Assert.Equal(!number, !it.IsYoung((int)rows.Cols[0].L[0]));
    }

    /// <summary>#112: a preview that stops at its row limit, or reads the journal to its end, of a live space whose last rows are not flushed yet releases what it derived: after the live rows are flushed nothing holds the interner.</summary>
    [Theory]
    [InlineData("5")]
    [InlineData(null)]
    public void A_preview_of_a_live_space_leaves_nothing_holding_the_interner(string? maxRows)
    {
        using var d = new RestartTests.Durable("memory");
        using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), new HostOptions { Projections = Repo.Projections, Cluster = "synth" });
        var events = LiveResumeTests.Arrival(EstateTests.EstateDir, seed: 4, distance: 12);
        LiveInput.Run(h, new LiveResumeTests.FakeSource(events[..^3]), new ReplayOptions { ChunkRows = 1000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = "synth", FlushAtEnd = false }, CancellationToken.None);
        for (var i = events.Count - 3; i < events.Count; i++) h.LiveEnvelope("eventhub:fake", LiveInput.EnvelopeText(events[i]), $"{{\"n\":{i}}}");
        Assert.True(h.Interner.Holding);                 // the last envelopes' rows are pending: no flush yet
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var scope = maxRows is null ? new System.Text.Json.Nodes.JsonObject() : new System.Text.Json.Nodes.JsonObject { ["maxRows"] = maxRows };
        var p = h.Preview(draft, new System.Text.Json.Nodes.JsonObject { ["scope"] = scope, ["graphs"] = new System.Text.Json.Nodes.JsonArray("serviceTree") });
        Assert.Equal(maxRows is null, p["scope"]!["complete"]!.GetValue<bool>());
        h.LiveFlush();
        Assert.False(h.Interner.Holding);
    }
}

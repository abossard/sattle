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
}

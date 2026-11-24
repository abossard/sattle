using Sattle.Cli;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>#41: the texts of rows that a step drops die with the step. A text an adapter row or a stateless step brings in is young; it becomes old only when it reaches state.</summary>
public class InternerTests
{
    const string Base = """
        {"module": "t.base", "version": "1.0.0", "imports": [],
         "entities": [{"name": "Event", "properties": [{"name": "id", "type": "Text"}, {"name": "tag", "type": "Text"}]}],
         "identities": [{"name": "event", "type": "Event", "components": [{"name": "id", "type": "Text"}]}],
         "rows": [{"name": "EventRow", "fields": [{"name": "id", "type": "Text"}, {"name": "kind", "type": "Text"}]}]}
        """;

    const string Ingest = """
        {"graph": "ingest", "ontology": "t.base", "inputs": [{"name": "events", "rows": "t.base.EventRow"}], "parameters": [],
         "verbs": [
           {"id": "events", "verb": "rows", "source": "events"},
           {"id": "tagged", "verb": "project", "columns": {"id": {"ref": "id"}, "kind": {"ref": "kind"}, "tag": {"fn": "concat", "args": [{"ref": "id"}, {"lit": "!"}]}}},
           {"id": "kept", "verb": "where", "condition": {"fn": "==", "args": [{"ref": "kind"}, {"lit": "kept"}]}},
           {"id": "observed", "verb": "nodes", "rule": "observed", "target": {"nodes": [
             {"var": "ev", "label": "Event", "id": {"identity": "event", "args": [{"ref": "id"}]}, "props": {"id": {"ref": "id"}, "tag": {"ref": "tag"}}}]}}],
         "wires": [{"from": "events", "to": "tagged"}, {"from": "tagged", "to": "kept"}, {"from": "kept", "to": "observed"}]}
        """;

    const string Space = """
        {"module": "t.space", "version": "1.0.0",
         "imports": [{"module": "t.base", "alias": "base", "version": "1.0.0", "digest": null}],
         "space": {"name": "s",
           "sources": [{"name": "events", "adapter": "record", "rows": "base.EventRow"}],
           "graphs": [{"name": "master", "projection": "base.ingest", "master": true, "inputs": {"events": "events"}}]}}
        """;

    /// <summary>Every text reads back as written and interns to one code, whatever form it is stored in: an element id packed, a text near one stored as it is, a wide text, an empty one, one longer than a page; and the same after a restore from the slots.</summary>
    [Theory]
    [InlineData("e:kmhdy6y657njixw3ienqx2wxw7")]
    [InlineData("r:aaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("z:77777777777777777777777777")]
    [InlineData("e:kmhdy6y657njixw3ienqx2wxw")]            // 25 characters after the colon
    [InlineData("e:kmhdy6y657njixw3ienqx2wxw71")]          // 27, and a 1
    [InlineData("e:Kmhdy6y657njixw3ienqx2wxw7")]           // an upper-case letter
    [InlineData("e:kmhdy6y657njixw3ienqx2wxw8")]           // an 8
    [InlineData("E:kmhdy6y657njixw3ienqx2wxw7")]
    [InlineData("e;kmhdy6y657njixw3ienqx2wxw7")]
    [InlineData("e:kmhdy6y657njixw3ienqx2wxw€")]           // two bytes per character
    [InlineData("")]
    [InlineData("long")]                                    // 70,000 characters, longer than a page
    public void A_text_reads_back_as_written_and_interns_to_one_code(string text)
    {
        if (text == "long") text = new string('x', 70_000);
        var it = new Interner();
        var others = new[] { "e:kmhdy6y657njixw3ienqx2wxw6", "e:kmhdy6y657njixw3ienqx2wxw", "x" };
        foreach (var o in others) it.Intern(o);
        var code = it.Intern(text);
        Assert.Equal(text, it.Get(code));
        Assert.Equal(code, it.Intern(text));
        Assert.Equal(others.Contains(text) ? 1 : 0, others.Count(o => it.TryFind(o, out var c) && c == code));
        var restored = new Interner();
        restored.ReplaceAll(it.Slots);
        Assert.True(restored.TryFind(text, out var again));
        Assert.Equal((code, text), (again, restored.Get(again)));
    }

    static Engine Start()
    {
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load([new SourceFile("t.base.module.gkql.json", Base), new SourceFile("t.base.ingest.gkql.json", Ingest), new SourceFile("t.space.module.gkql.json", Space)], updatePins: true);
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
        using var h = SpaceHost.OpenDurable(d.Open(), EstateTests.Files(demo: true), new HostOptions { Projections = Repo.Projections});
        var events = LiveResumeTests.Arrival(EstateTests.EstateDir, seed: 4, distance: 12);
        LiveInput.Run(h, new LiveResumeTests.FakeSource(events[..^3]), new ReplayOptions { ChunkRows = 1000, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, FlushAtEnd = false }, CancellationToken.None);
        for (var i = events.Count - 3; i < events.Count; i++) h.LiveEnvelope("eventhub:fake", LiveInput.EnvelopeText(events[i]), $"{{\"n\":{i}}}");
        Assert.True(h.Interner.Holding);                 // the last envelopes' rows are pending: no flush yet
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var scope = maxRows is null ? new System.Text.Json.Nodes.JsonObject() : new System.Text.Json.Nodes.JsonObject { ["maxRows"] = maxRows };
        var p = h.Preview(draft, new System.Text.Json.Nodes.JsonObject { ["scope"] = scope, ["graphs"] = new System.Text.Json.Nodes.JsonArray("serviceTree") });
        Assert.Equal(maxRows is null, p["scope"]!["complete"]!.GetValue<bool>());
        h.LiveFlush();
        Assert.False(h.Interner.Holding);
    }

    /// <summary>#158 (from #157): the engine of a preview and of a commit shares the host's interner and reads the kept batches again. Whatever the cut of the input into batches, a preview of an edited view shows, and its commit gives, the state of a space that ran the edited view from the start.</summary>
    [Theory, Trait("Category", "Slow")]
    [InlineData(1)] [InlineData(30)] [InlineData(2000)]
    public void A_reactivation_on_the_shared_interner_gives_the_edited_space_whatever_the_batch_cut(int chunk)
    {
        const string Readiness = "acme.readiness.readiness.gkql.json";
        var files = EstateTests.Files(demo: true);
        var edited = files.Single(f => f.Path.EndsWith(Readiness, StringComparison.Ordinal)).Text.Replace("\"minReady\", \"kind\": \"Integer\", \"default\": 0", "\"minReady\", \"kind\": \"Integer\", \"default\": 1000");          // no workload has that many ready instances: every state turns NotReady
        SpaceHost Run(IReadOnlyList<SourceFile> definitions)
        {
            var h = new SpaceHost(definitions, new HostOptions { Projections = Repo.Projections });
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = chunk, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000 }).Run(EstateTests.EstateDir);
            return h;
        }
        using var fromStart = Run([.. files.Select(f => f.Path.EndsWith(Readiness, StringComparison.Ordinal) ? f with { Text = edited } : f)]);
        using var h = Run(files);
        Assert.NotEqual(fromStart.StateDigest(), h.StateDigest());          // the edit changes the space
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.True(h.SetDraftText(draft, Readiness, edited, 0)["ok"]!.GetValue<bool>());
        var preview = h.Preview(draft, new System.Text.Json.Nodes.JsonObject { ["include"] = new System.Text.Json.Nodes.JsonArray("counts") });
        Assert.True(preview["scope"]!["complete"]!.GetValue<bool>());
        var expected = fromStart.Liveness()["graphs"]!.AsArray().Where(g => g!["name"]!.GetValue<string>() != "catalog").ToDictionary(g => g!["name"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>());
        Assert.Equal(expected, preview["graphs"]!.AsArray().ToDictionary(g => g!["id"]!.GetValue<string>(), g => g!["digest"]!.GetValue<string>()));
        h.Commit(Readiness, edited, h.CatalogRevision);
        Assert.Equal(fromStart.StateDigest(), h.StateDigest());
    }
}

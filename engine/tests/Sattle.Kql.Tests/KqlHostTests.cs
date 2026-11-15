using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>A space served with projections in KQL: the `.kql` text is the only text of such a projection. A printed module leaves it out, a definition commit takes the file by its name, and a restart keeps it.</summary>
public class KqlHostTests
{
    static readonly string Definitions = Path.Combine(FindRoot(), "engine", "definitions");

    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static HostOptions Options => new() { Cluster = "synth", Projections = new KqlSource() };

    static readonly Lazy<SpaceHost> Served = new(() => new SpaceHost(Files, Options));

    /// <summary>Each module of the estate that has a projection in KQL, printed by the host: the projection is not in the text, and the text checks as no change.</summary>
    [Theory]
    [InlineData("acme.svc", "serviceDeps")]
    [InlineData("acme.tree", "treeStructure")]
    [InlineData("acme.treehealth", "treeRollup")]
    [InlineData("acme.workload", "workloadHealth")]
    [InlineData("acme.ops", "ingest")]
    public void A_printed_module_leaves_out_its_projection_in_KQL(string module, string projection)
    {
        var h = Served.Value;
        var text = h.Definition(module);
        Assert.DoesNotContain($"projection {projection} ", text);
        var check = h.Check(module, text);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.Empty(check["affected"]!["modules"]!.AsArray());
        Assert.Equal(File.ReadAllText(Path.Combine(Definitions, $"{module}.{projection}.kql")), h.Definition($"{module}.{projection}.kql"));
    }

    static List<SourceFile> Files => Catalog.ReadDirectory(Definitions);
    /// <summary>The definitions with every projection in the engine's own notation, as they were before the projections moved to KQL.</summary>
    static string SattleProjections => Path.Combine(Path.GetDirectoryName(Definitions)!, "tests", "Sattle.Tests", "fixtures", "sattle-projections");
    static string EstateDir => Path.Combine(FindRoot(), "engine", "tests", "Sattle.Tests", "fixtures", "estate");
    static string Text(string file) => File.ReadAllText(Path.Combine(Definitions, file));
    static void Feed(SpaceHost h) => new Replay(h, h.Space, h.Adapters, new ReplayOptions { Cluster = "synth" }).Run(EstateDir);
    static int Contains(SpaceHost h) => h.Engine.Graph("serviceTree").State.Rels.Count(r => Ids.Name(r.Type) == "Contains");

    /// <summary>A commit names a `.kql` file as it names a module: the service tree no longer contains the services of one namespace after it, and a restart from the journal and from a seal keeps the file's new text.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_commit_of_a_KQL_file_changes_its_graph_and_a_restart_keeps_it(bool seal)
    {
        const string file = "acme.svc.serviceDeps.kql";
        var edited = Text(file).Replace("edges Contains from group to service = services |", "edges Contains from group to service = services | where namespace != \"shop\" |");
        Assert.NotEqual(Text(file), edited);
        var store = new InMemoryGraphStore();
        string catalog, state;
        using (var h = SpaceHost.OpenDurable(store, Files, Options))
        {
            Feed(h);
            var before = Contains(h);
            h.Commit(file, edited, h.CatalogRevision);
            Assert.InRange(Contains(h), 0, before - 1);
            Assert.Equal(edited, h.Definition(file));
            if (seal) h.Seal();
            (catalog, state) = (h.Catalog.Graph.Digest(), h.StateDigest());
        }
        using var r = SpaceHost.OpenDurable(store, null, Options);
        Assert.Equal(seal, r.Restart!.FromSeal);
        Assert.Equal((catalog, state, edited), (r.Catalog.Graph.Digest(), r.StateDigest(), r.Definition(file)));
    }

    /// <summary>A check of a `.kql` file that does not lower names the file and the line of the error.</summary>
    [Fact]
    public void A_check_of_a_KQL_file_places_its_errors_in_that_file()
    {
        var text = Text("acme.svc.serviceDeps.kql");
        var line = text[..text.IndexOf("name = s.name", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        var check = Served.Value.Check("acme.svc.serviceDeps.kql", text.Replace("name = s.name", "name = s.title"));
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), d => d!["file"]!.GetValue<string>() == "acme.svc.serviceDeps.kql" && d["line"]!.GetValue<int>() == line);
    }

    /// <summary>A name from a request that is no plain name is refused before anything is read or written, so no file is placed outside the definitions folder.</summary>
    [Theory]
    [InlineData("../acme.svc.serviceDeps.kql")]
    [InlineData("sub/acme.svc.serviceDeps.kql")]
    [InlineData("..")]
    public void A_definition_name_that_is_no_plain_name_is_refused(string name)
    {
        var h = Served.Value;
        Assert.Equal("BadName", Assert.Throws<HostError>(() => h.Check(name, Text("acme.svc.serviceDeps.kql"))).Code);
        Assert.Equal("BadName", Assert.Throws<HostError>(() => h.Commit(name, Text("acme.svc.serviceDeps.kql"), h.CatalogRevision)).Code);
    }

    /// <summary>A `.kql` file of a module the space does not have is read by no module: an error with its name.</summary>
    [Fact]
    public void A_KQL_file_of_a_missing_module_is_refused()
    {
        var check = Served.Value.Check("acme.nothing.serviceDeps.kql", Text("acme.svc.serviceDeps.kql"));
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "UnreadFile" && d["file"]!.GetValue<string>() == "acme.nothing.serviceDeps.kql");
    }

    /// <summary>A draft edits a `.kql` text: its check is the text's, its module texts stay without the projection, and its commit changes the graph as a commit of the file does.</summary>
    [Fact]
    public void A_draft_edits_a_KQL_file_and_commits_it()
    {
        const string file = "acme.svc.serviceDeps.kql";
        var edited = Text(file).Replace("edges Contains from group to service = services |", "edges Contains from group to service = services | where namespace != \"shop\" |");
        using var h = new SpaceHost(Files, Options);
        Feed(h);
        var before = Contains(h);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var check = h.SetDraftText(d, file, edited, 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.True(check["modules"]![file]!["changed"]!.GetValue<bool>());
        Assert.Equal((edited, h.Definition("acme.svc")), (h.DraftText(d, file), h.DraftText(d, "acme.svc")));
        Assert.Equal(before, Contains(h));                                   // the draft changes nothing live
        h.CommitDraft(d, h.CatalogRevision);
        Assert.InRange(Contains(h), 0, before - 1);
        Assert.Equal(edited, h.Definition(file));
    }

    /// <summary>A draft text that does not lower is kept, so an editor never loses what was typed; the check names the file and the line, and the draft cannot commit.</summary>
    [Fact]
    public void A_draft_keeps_a_KQL_text_that_does_not_lower_and_places_its_errors()
    {
        const string file = "acme.svc.serviceDeps.kql";
        var text = Text(file);
        var line = text[..text.IndexOf("name = s.name", StringComparison.Ordinal)].Count(c => c == '\n') + 1;
        var wrong = text.Replace("name = s.name", "name = s.title");
        var h = Served.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var check = h.SetDraftText(d, file, wrong, 0);
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), x => x!["file"]!.GetValue<string>() == file && x["line"]!.GetValue<int>() == line);
        Assert.Equal((wrong, "1"), (h.DraftText(d, file), h.DraftInfo(d)["revision"]!.GetValue<string>()));
        Assert.Equal("Invalid", Assert.Throws<HostError>(() => h.CommitDraft(d, h.CatalogRevision)).Code);
        h.DiscardDraft(d);
    }

    /// <summary>A structural edit of a projection written in KQL would be lost at the next commit, which is made from text: refused, with the file to edit.</summary>
    [Fact]
    public void A_structural_edit_of_a_projection_in_KQL_is_refused()
    {
        var h = Served.Value;
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        // a new operator of serviceDeps, made from one of its own
        const string projection = "d:acme.svc/projection:serviceDeps", id = projection + "/op:first";
        var op = h.DraftSnapshot(d)["extent"]!["entities"]!.AsArray().First(e => e!["id"]!["value"]!.GetValue<string>().StartsWith(projection + "/op:", StringComparison.Ordinal))!.DeepClone();
        op["id"] = new JsonObject { ["value"] = id };
        var e = Assert.Throws<HostError>(() => h.ApplyDraftPatch(d, new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["kind"] = "entity", ["id"] = id, ["after"] = op }) }, 0));
        Assert.Equal(("KqlProjection", "acme.svc.serviceDeps.kql"), (e.Code, e.Extra["file"]!.GetValue<string>()));
        Assert.Equal("0", h.DraftInfo(d)["revision"]!.GetValue<string>());
        h.DiscardDraft(d);
    }

    /// <summary>A draft's text is read with the draft's own modules and the projection files it was made with: a commit of another module after the draft was made does not enter the draft's patch, and the draft still commits.</summary>
    [Fact]
    public void A_draft_text_set_after_a_commit_elsewhere_takes_nothing_of_that_commit()
    {
        const string file = "acme.svc.serviceDeps.kql";
        using var h = new SpaceHost(Files, Options);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var other = File.ReadAllText(Path.Combine(Definitions, "acme.deployhealth.sattle")).Replace("param Integer = 0", "param Integer = 1");
        h.Commit("acme.deployhealth", other, h.CatalogRevision);
        var check = h.SetDraftText(d, file, Text(file).Replace("edges Contains from group to service = services |", "edges Contains from group to service = services | where namespace != \"shop\" |"), 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        var ids = h.DraftPatches(d, 0).SelectMany(p => p["edits"]!.AsArray()).Select(e => e!["id"]!["value"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("d:acme.deployhealth", StringComparison.Ordinal));
        h.CommitDraft(d, h.CatalogRevision);
    }

    /// <summary>An edge from outside into a projection written in KQL belongs to the other text: a patch that adds a graph to the space, produced by the projection, is accepted.</summary>
    [Fact]
    public void A_patch_adding_a_graph_produced_by_a_projection_in_KQL_is_accepted()
    {
        using var h = new SpaceHost(Files, Options);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var extent = h.DraftSnapshot(d)["extent"]!;
        static string V(JsonNode? n) => n!["value"]!.GetValue<string>();
        var g = V(extent["relationships"]!.AsArray().First(r => V(r!["type"]) == "producedBy" && V(r["target"]) == "d:acme.svc/projection:serviceDeps")!["source"]);
        // the graph, and its bindings, which sit beside it in the space: `binding:serviceTree.master`
        var bindings = g.Replace("/graph:", "/binding:") + ".";
        bool Mine(string id) => id == g || id.StartsWith(g + "/", StringComparison.Ordinal) || id.StartsWith(bindings, StringComparison.Ordinal);
        JsonNode Copy(JsonNode e) => JsonNode.Parse(e.ToJsonString().Replace(g, g + "Two").Replace(bindings, bindings[..^1] + "Two."))!;
        var entities = extent["entities"]!.AsArray().Where(e => Mine(V(e!["id"]))).Select(e => Copy(e!)).ToList();
        foreach (var p in entities.Single(e => V(e["id"]) == g + "Two")["properties"]!.AsArray().Where(p => V(p!["property"]) == "name")) p!["value"]!["value"] = "serviceTreeTwo";
        var rels = extent["relationships"]!.AsArray().Where(r => Mine(V(r!["source"])) || V(r["type"]) == "contains" && Mine(V(r["target"]))).Select(r => Copy(r!)).ToList();
        Assert.Contains(rels, r => V(r["type"]) == "producedBy");
        var patch = new JsonObject { ["edits"] = new JsonArray([.. entities.Select(e => (JsonNode)new JsonObject { ["kind"] = "entity", ["id"] = V(e["id"]), ["after"] = e }),
            .. rels.Select(r => (JsonNode)new JsonObject { ["kind"] = "relationship", ["id"] = V(r["id"]), ["after"] = r })]) };
        var check = h.ApplyDraftPatch(d, patch, 0);
        Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
        Assert.Contains("graph serviceTreeTwo = svc.serviceDeps(master: master);", h.DraftText(d, "acme.space"));
    }

    /// <summary>`sattle serve` builds its host with the `.kql` files and a source that reads them: it serves the estate to the state of the projections' former text, and its definitions are the `.kql` texts.
    /// The host is built from the helpers `serve` uses (the files, the host options), without starting the server.</summary>
    [Fact]
    public void Serve_with_projections_in_KQL_serves_the_state_of_their_former_text()
    {
        string Served(string defs)
        {
            var o = Sattle.Cli.Program.Options.Parse(["serve", EstateDir, "--cluster", "synth", "--defs", defs]);
            using var host = new SpaceHost(Sattle.Cli.Program.ReadDefinitions(o), Sattle.Cli.Program.HostOptionsFor(o, "synth", null));
            if (defs == Definitions) Assert.Equal(Text("acme.svc.serviceDeps.kql"), host.Definition("acme.svc.serviceDeps.kql"));
            new Replay(host, host.Space, host.Adapters, Sattle.Cli.Program.ReplayOptionsOf(o, "synth")).Run(EstateDir);
            host.Finish();
            return host.StateDigest();
        }
        Assert.Equal(Served(SattleProjections), Served(Definitions));
    }

    /// <summary>A store written with projections in KQL, opened without a projection source, is refused, naming the files it holds. The other way round, a store without projection files opened with a source loads today's text: the source has nothing to read, so no projection is held twice.</summary>
    [Fact]
    public void A_store_written_with_projections_in_KQL_is_refused_without_a_source()
    {
        var store = new InMemoryGraphStore();
        using (SpaceHost.OpenDurable(store, Files, Options)) { }
        var e = Assert.Throws<InvalidOperationException>(() => SpaceHost.OpenDurable(store, null, Options with { Projections = null }));
        Assert.Contains("projection source", e.Message);
        Assert.Contains("acme.svc.serviceDeps.kql", e.Message);
        var plain = new InMemoryGraphStore();
        string catalog;
        using (var h = SpaceHost.OpenDurable(plain, Catalog.ReadDirectory(SattleProjections), Options with { Projections = null })) catalog = h.Catalog.Graph.Digest();
        using var r = SpaceHost.OpenDurable(plain, null, Options);
        Assert.Equal(catalog, r.Catalog.Graph.Digest());
    }

    /// <summary>Each operator of a preview's rows is named by an id the outline of its projection's text shows (on an operator, a step's sink or a
    /// function's call), so an editor puts the rows after each operator under its card (research 11, section 2 e).</summary>
    [Fact]
    public void The_operators_of_a_preview_are_named_by_ids_the_outline_shows()
    {
        using var h = new SpaceHost(Files, Options);
        var d = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var ops = h.Preview(d, new JsonObject { ["include"] = new JsonArray("rows"), ["rowLimit"] = "1" })["graphs"]!.AsArray()
            .SelectMany(g => g!["operators"]?.AsArray() ?? []).Select(o => o!["op"]!.GetValue<string>()).ToList();
        var projections = ops.GroupBy(op => op[..op.IndexOf("/op:", StringComparison.Ordinal)]).ToList();
        Assert.Equal(Directory.EnumerateFiles(Definitions, "*.kql").Count(), projections.Count);
        foreach (var projection in projections)
        {
            var (module, name) = (projection.Key[2..projection.Key.IndexOf('/', StringComparison.Ordinal)], projection.Key[(projection.Key.LastIndexOf(':') + 1)..]);
            var shown = new HashSet<string>(StringComparer.Ordinal);
            void Walk(JsonNode? n)
            {
                if (n is JsonArray a) foreach (var x in a) Walk(x);
                if (n is not JsonObject o) return;
                foreach (var (k, v) in o)
                    if (k is "engine" or "expands") foreach (var id in v!.AsArray()) shown.Add(id!.GetValue<string>());
                    else if (k == "sink" && v is not null) shown.Add(v.GetValue<string>());
                    else Walk(v);
            }
            Walk(KqlAuthoring.Analyse(h.Catalog, $"{module}.{name}", Text($"{module}.{name}.kql"))["statements"]);
            Assert.Subset(shown, projection.ToHashSet(StringComparer.Ordinal));
        }
    }
}

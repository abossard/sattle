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

    static List<SourceFile> Files => [.. Catalog.ReadDirectory(Definitions), .. KqlFiles.ReadDirectory(Definitions)];
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
}

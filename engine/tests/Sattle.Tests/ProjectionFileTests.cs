using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The text of a projection is a definition file of the space beside the module texts, written in a registered language (PROTOCOL `/definitions`, the seam for several languages). The host names each such file, prints its module without the projection, checks and commits it by its file name, writes it back as committed, and refuses a name that is no plain name, a file no module reads, and a folder or a store whose projection files it has no source for. Said for the projection files of the examples, whatever their language.</summary>
public class ProjectionFileTests
{
    static readonly Lazy<SpaceHost> Fresh = new(HostTests.NewHost);
    static HostOptions Options => new() { Projections = Repo.Projections};
    static string Text(string file) => File.ReadAllText(Path.Combine(Repo.Definitions, file));
    static bool IsProjectionFile(string path) => !path.EndsWith(".module.gkql.json", StringComparison.Ordinal);
    static string Deps => Fresh.Value.ProjectionFile("acme.svc.serviceDeps");

    /// <summary>Every projection file of the folder is served under its module with the text of the file; the module is printed without the projection, and the printed text checks as no change.</summary>
    [Fact]
    public void Every_projection_file_is_served_as_it_is_and_its_module_is_printed_without_the_projection()
    {
        var h = Fresh.Value;
        var served = new List<string>();
        foreach (var module in h.Definitions().AsArray())
        {
            var name = module!["name"]!.GetValue<string>();
            var files = module["projections"]!.AsArray();
            if (files.Count == 0) continue;
            var text = h.Definition(name);
            var check = h.Check(name, text);
            Assert.True(check["ok"]!.GetValue<bool>(), check["diagnostics"]!.ToJsonString());
            Assert.Empty(check["affected"]!["modules"]!.AsArray());
            foreach (var p in files)
            {
                var file = p!["file"]!.GetValue<string>();
                Assert.DoesNotContain("\"verbs\"", text);
                Assert.Equal(Text(file), h.Definition(file));
                served.Add(file);
            }
        }
        Assert.Equal(Directory.EnumerateFiles(Repo.Definitions).Select(f => Path.GetFileName(f)!).Where(IsProjectionFile).Order(StringComparer.Ordinal), served.Order(StringComparer.Ordinal));
        Assert.NotEmpty(served);
    }

    /// <summary>A name from a request that is no plain name is refused before anything is read or written, so no file is placed outside the definitions folder.</summary>
    [Theory]
    [InlineData("../")] [InlineData("sub/")] [InlineData("..")]
    public void A_definition_name_that_is_no_plain_name_is_refused(string prefix)
    {
        var h = Fresh.Value;
        var name = prefix == ".." ? ".." : prefix + Deps;
        Assert.Equal("BadName", Assert.Throws<HostError>(() => h.Check(name, Text(Deps))).Code);
        Assert.Equal("BadName", Assert.Throws<HostError>(() => h.Commit(name, Text(Deps), h.CatalogRevision)).Code);
    }

    /// <summary>A projection file of a module the space does not have is read by no module: an error with its name.</summary>
    [Fact]
    public void A_projection_file_of_a_missing_module_is_refused()
    {
        var file = Deps.Replace("acme.svc.", "acme.nothing.");
        var check = Fresh.Value.Check(file, Text(Deps));
        Assert.False(check["ok"]!.GetValue<bool>());
        Assert.Contains(check["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "UnreadFile" && d["file"]!.GetValue<string>() == file);
    }

    /// <summary>A catalog or a host given no projection source says so for each projection file, instead of only failing the space's references.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Definitions_loaded_without_a_projection_source_name_each_projection_file_and_what_is_missing(bool host)
    {
        var files = Catalog.ReadDirectory(Repo.Definitions);
        var message = host ? Assert.Throws<InvalidOperationException>(() => new SpaceHost(files, new HostOptions { })).Message
            : string.Join("\n", new Catalog().Load(files).Diagnostics.Select(d => d.Message));
        var projectionFiles = files.Where(f => IsProjectionFile(f.Path)).ToList();
        Assert.NotEmpty(projectionFiles);
        foreach (var file in projectionFiles)
            Assert.Contains($"{Path.GetFileName(file.Path)}: this catalog has no projection source to read it", message);
    }

    /// <summary>A store written with projection files, opened without a projection source, is refused, naming the files it holds.</summary>
    [Fact]
    public void A_store_written_with_projection_files_is_refused_without_a_source()
    {
        var store = new InMemoryGraphStore();
        using (SpaceHost.OpenDurable(store, Catalog.ReadDirectory(Repo.Definitions), Options)) { }
        var e = Assert.Throws<InvalidOperationException>(() => SpaceHost.OpenDurable(store, null, Options with { Projections = null }));
        Assert.Contains("projection source", e.Message);
        Assert.Contains(Deps, e.Message);
    }

    /// <summary>A commit writes the module texts, which hold no projection, and each projection file as it was committed; a host started from the written folder serves the same catalog.</summary>
    [Fact]
    public void A_committed_projection_file_is_written_as_committed_and_a_host_on_the_written_folder_serves_the_same_catalog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-defs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Repo.Definitions)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            using var h = new SpaceHost(Catalog.ReadDirectory(dir), Options) { DefinitionsDir = dir, WriteDefinitions = true };
            var edited = Text(Deps).Replace("\"below\": {\"lit\": 0.02}, \"enterAfter\": {\"lit\": 3}, \"exitAfter\": {\"lit\": 3}", "\"below\": {\"lit\": 0.0}, \"enterAfter\": {\"lit\": 3}, \"exitAfter\": {\"lit\": 30}");
            Assert.NotEqual(Text(Deps), edited);
            h.Commit(Deps, edited, h.CatalogRevision);
            Assert.Equal(edited, File.ReadAllText(Path.Combine(dir, Deps)));
            Assert.All(Directory.EnumerateFiles(dir, "*.module.gkql.json"), f => Assert.DoesNotContain("\"verbs\"", File.ReadAllText(f)));
            using var again = new SpaceHost(Catalog.ReadDirectory(dir), Options);
            Assert.Equal(h.Snapshot("catalog", null)["extent"]!.ToJsonString(), again.Snapshot("catalog", null)["extent"]!.ToJsonString());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>gkql is the only language: a module text or a projection in another language, given to a check, a commit or a draft, is refused with one typed error each (a draft knows no file of that name), and nothing changes.</summary>
    [Theory]
    [InlineData("acme.svc", "module acme.svc @ 1.0.0;", "ModuleText ModuleText ModuleText")]
    [InlineData("acme.svc.sattle", "module acme.svc @ 1.0.0;", "ModuleText ModuleText BadRequest")]
    [InlineData("acme.svc.serviceDeps.kql", "graph serviceDeps : sattle.infra with (rule = \"r\") = flows | project a = 1;", "ModuleText ModuleText BadRequest")]
    public void A_definition_in_another_language_is_refused_with_one_typed_error(string name, string text, string expected)
    {
        var h = Fresh.Value;
        var revision = h.CatalogRevision;
        static string Codes(JsonNode r) => r["ok"]!.GetValue<bool>() ? "ok" : string.Join("+", r["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal));
        static string Outcome(Func<JsonNode> call) { try { return Codes(call()); } catch (HostError e) { return e.Code; } }
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var got = string.Join(" ", Outcome(() => h.Check(name, text)), Outcome(() => h.Commit(name, text, h.CatalogRevision)), Outcome(() => h.SetDraftText(draft, name, text, 0)));
        h.DiscardDraft(draft);
        Assert.Equal(expected, got);
        Assert.Equal(revision, h.CatalogRevision);
    }

    /// <summary>A file of another language in the definitions folder is no definition: the load names it with one typed error, and nothing reads it.</summary>
    [Theory]
    [InlineData("acme.extra.sattle", "module acme.extra @ 1.0.0;")]
    [InlineData("acme.svc.extra.kql", "graph extra : sattle.infra with (rule = \"r\") = flows | project a = 1;")]
    public void A_file_of_another_language_in_the_definitions_is_unread(string file, string text)
    {
        var errors = new Catalog(Repo.Projections).Load([.. Catalog.ReadDirectory(Repo.Definitions), new SourceFile(file, text)], updatePins: true).Diagnostics.Where(d => d.IsError).ToList();
        Assert.Equal(("UnreadFile", file), (Assert.Single(errors).Code, errors[0].File));
    }
}

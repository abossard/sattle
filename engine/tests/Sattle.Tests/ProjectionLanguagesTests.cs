using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>#106: projection languages behind one seam. A second, tiny language loads next to gkql in one space; each file is read by the one source that claims it.</summary>
public class ProjectionLanguagesTests
{
    static string Definitions => Repo.Definitions;

    /// <summary>A language of one statement, `heading "Text" from input : module`, in a file `module.projection.heading`: a document projection with one heading block. It stands for a gkql program and is lowered by the gkql source; places and diagnostics name the `.heading` file.
    /// What it proves: the seam dispatches files by kind to their source, merges the facts of two sources, a space binds a projection of each, and the protocol names each file's language. It does not prove a language whose lowering is independent of the gkql front end: it reuses that front end to produce its facts. `alsoGkql` makes it claim gkql programs too, for the conflict.</summary>
    sealed class HeadingSource(bool alsoGkql = false) : IProjectionSource
    {
        const string Program = ".gkql.json";
        static readonly Regex Statement = new(@"^heading ""(?<text>[^""]*)"" from (?<input>\w+) : (?<module>[\w.]+)$");
        public string? Language(string fileName) => fileName.EndsWith(".heading", StringComparison.Ordinal) || alsoGkql && fileName.EndsWith(Program, StringComparison.Ordinal) && !fileName.EndsWith(".module" + Program, StringComparison.Ordinal) ? "heading" : null;
        public ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files)
        {
            var programs = new List<SourceFile>(); var diags = new List<Diagnostic>();
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f.Path);
                if (name.LastIndexOf('.') is var dot && (dot <= 0 || name[..dot] != module)) continue;
                var m = Statement.Match(f.Text.Trim());
                if (!m.Success) { diags.Add(new Diagnostic("Heading", "error", "", "expected: heading \"text\" from input : module", f.Path, 1, 1)); continue; }
                programs.Add(new SourceFile(f.Path[..^".heading".Length] + Program, $$"""
                    {"graph": "{{name[(dot + 1)..]}}", "ontology": "sattle.doc", "description": "A heading.", "history": 1,
                     "inputs": [{"name": "{{m.Groups["input"]}}", "ontology": "{{m.Groups["module"]}}"}], "parameters": [],
                     "verbs": [
                       {"id": "block", "verb": "values", "values": {"columns": {"k": "Text"}, "rows": [{"k": "title"}] } },
                       {"id": "heading", "verb": "nodes", "target": {"nodes": [{"var": "n", "label": "Block", "id": {"identity": "block", "args": [{"lit": "title"}, {"ref": "k"}]},
                         "props": {"kind": {"lit": "heading"}, "order": {"lit": "0"}, "level": {"lit": 1}, "text": {"lit": "{{m.Groups["text"]}}" } } } ] } } ],
                     "wires": [{"from": "block", "to": "heading"}]}
                    """));
            }
            var low = Repo.Projections.Lower(catalog, module, programs);
            static string? Back(string? p) => p is not null && p.EndsWith(Program, StringComparison.Ordinal) ? p[..^Program.Length] + ".heading" : p;
            return new ProjectionFacts(low.Projections, low.Places.ToDictionary(kv => kv.Key, kv => (Back(kv.Value.File)!, kv.Value.Pos)), [.. diags, .. low.Diagnostics.Select(d => d with { File = Back(d.File) })]);
        }
    }

    static readonly IProjectionSource Both = new ProjectionSources(Repo.Projections, new HeadingSource());

    /// <summary>The definitions folder with a heading projection in acme.report, bound by the acme space next to the report written in gkql.</summary>
    static List<SourceFile> Files()
    {
        var files = Catalog.ReadDirectory(Definitions);
        var space = files.FindIndex(f => Path.GetFileName(f.Path) == "acme.space.module.gkql.json");
        var module = JsonNode.Parse(files[space].Text)!;
        module["space"]!["graphs"]!.AsArray().Add(new JsonObject { ["name"] = "title", ["projection"] = "rpt.title", ["inputs"] = new JsonObject { ["health"] = "readiness" } });
        files[space] = files[space] with { Text = module.ToJsonString() };
        files.Add(new SourceFile(Path.Combine(Definitions, "acme.report.title.heading"), "heading \"Unit readiness\" from health : acme.readiness\n"));
        return files;
    }

    [Fact]
    public void Two_languages_load_side_by_side_in_one_space_and_the_protocol_names_each_file_s_language()
    {
        using var h = new SpaceHost(Files(), new HostOptions { Projections = Both });
        Assert.Contains(h.Space.Graphs, g => g.Name == "title");
        Assert.Contains(h.Space.Graphs, g => g.Name == "readinessReport");
        var report = h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.report")!["projections"]!.AsArray();
        Assert.Equal(["acme.report.readinessReport acme.report.readinessReport.gkql.json gkql", "acme.report.title acme.report.title.heading heading"],
            report.Select(p => $"{p!["projection"]} {p["file"]} {p["language"]}"));
    }

    [Theory]
    [InlineData("acme.report.readinessReport.gkql.json", "gkql")]
    [InlineData("acme.report.title.heading", "heading")]
    [InlineData("acme.report.module.gkql.json", null)]
    [InlineData("notes.md", null)]
    public void The_language_of_a_file_is_that_of_the_source_that_claims_it(string file, string? language) => Assert.Equal(language, Both.Language(file));

    [Fact]
    public void A_file_two_sources_claim_is_an_error_that_names_the_file_once()
    {
        var both = new ProjectionSources(Repo.Projections, new HeadingSource(alsoGkql: true));
        Assert.Equal("gkql", both.Language("acme.report.readinessReport.gkql.json"));     // still a projection file, not a module document
        var res = new Catalog(both).Load(Files(), updatePins: true);
        var claimed = res.Diagnostics.Where(d => d.Code == "ProjectionFile" && d.IsError).ToList();
        var report = Assert.Single(claimed, d => Path.GetFileName(d.File) == "acme.report.readinessReport.gkql.json");
        Assert.Equal("acme.report.readinessReport.gkql.json is claimed by 2 projection languages: gkql, heading", report.Message);
        Assert.DoesNotContain(claimed, d => Path.GetFileName(d.File) == "acme.report.title.heading");
    }

    [Fact]
    public void A_statement_the_language_does_not_know_is_reported_in_its_file()
    {
        var files = Files();
        files[^1] = files[^1] with { Text = "headline Unit readiness\n" };
        var res = new Catalog(Both).Load(files, updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.Code == "Heading" && Path.GetFileName(d.File) == "acme.report.title.heading");
    }
}

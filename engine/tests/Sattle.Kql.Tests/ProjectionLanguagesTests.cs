using System.Text.RegularExpressions;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Kql.Tests;

/// <summary>#106: projection languages behind one seam. A second, tiny language loads next to KQL in one space; each file is read by the one source that claims it.</summary>
public class ProjectionLanguagesTests
{
    static readonly string Definitions = Path.Combine(Repo(), "engine", "definitions");
    static string Repo() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (!Directory.Exists(Path.Combine(d!.FullName, "engine", "definitions"))) d = d.Parent; return d.FullName; }

    /// <summary>A language of one statement, `heading "Text" from input : module`, in a file `module.projection.heading`: a document projection with one heading block. It stands for a KQL text and is lowered by the KQL source; places and diagnostics name the `.heading` file.
    /// What it proves: the seam dispatches files by kind to their source, merges the facts of two sources, a space binds a projection of each, and the protocol names each file's language. It does not prove a language whose lowering is independent of the KQL front end: it reuses that front end to produce its facts. `alsoKql` makes it claim `.kql` files too, for the conflict.</summary>
    sealed class HeadingSource(bool alsoKql = false) : IProjectionSource
    {
        static readonly Regex Statement = new(@"^heading ""(?<text>[^""]*)"" from (?<input>\w+) : (?<module>[\w.]+)$");
        public string? Language(string fileName) => fileName.EndsWith(".heading", StringComparison.Ordinal) || alsoKql && fileName.EndsWith(".kql", StringComparison.Ordinal) ? "heading" : null;
        public ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files)
        {
            var kql = new List<SourceFile>(); var diags = new List<Diagnostic>();
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f.Path);
                if (name.LastIndexOf('.') is var dot && (dot <= 0 || name[..dot] != module)) continue;
                var m = Statement.Match(f.Text.Trim());
                if (!m.Success) { diags.Add(new Diagnostic("Heading", "error", "", "expected: heading \"text\" from input : module", f.Path, 1, 1)); continue; }
                kql.Add(new SourceFile(Path.ChangeExtension(f.Path, ".kql"), $$"""
                    graph {{name[(dot + 1)..]}} : sattle.doc with (description = "A heading.", history = 1);
                    input {{m.Groups["input"]}} : {{m.Groups["module"]}};
                    nodes Block on block = datatable(k:string) ["title"]
                        | project block = identity("block", "title", k), ['kind'] = "heading", ['order'] = "0", level = 1, text = "{{m.Groups["text"]}}";
                    """));
            }
            var low = new KqlSource().Lower(catalog, module, kql);
            static string? Back(string? p) => p is null ? null : Path.ChangeExtension(p, ".heading");
            return new ProjectionFacts(low.Projections, low.Places.ToDictionary(kv => kv.Key, kv => (Back(kv.Value.File)!, kv.Value.Pos)), [.. diags, .. low.Diagnostics.Select(d => d with { File = Back(d.File) })]);
        }
    }

    static readonly IProjectionSource Both = new ProjectionSources(new KqlSource(), new HeadingSource());

    /// <summary>The definitions folder with a heading projection in acme.report, bound by the acme space next to the report written in KQL.</summary>
    static List<SourceFile> Files()
    {
        var files = Catalog.ReadDirectory(Definitions);
        var space = files.FindIndex(f => Path.GetFileName(f.Path) == "acme.space.sattle");
        files[space] = files[space] with { Text = files[space].Text.Replace("  graph deploymentReport = rpt.deploymentReport(health: deploymentHealth);\n", "  graph deploymentReport = rpt.deploymentReport(health: deploymentHealth);\n  graph title = rpt.title(health: deploymentHealth);\n", StringComparison.Ordinal) };
        Assert.Contains("graph title", files[space].Text, StringComparison.Ordinal);
        files.Add(new SourceFile(Path.Combine(Definitions, "acme.report.title.heading"), "heading \"Deployment health\" from health : acme.deployhealth\n"));
        return files;
    }

    [Fact]
    public void Two_languages_load_side_by_side_in_one_space_and_the_protocol_names_each_file_s_language()
    {
        using var h = new SpaceHost(Files(), new HostOptions { Cluster = "synth", Projections = Both });
        Assert.Contains(h.Space.Graphs, g => g.Name == "title");
        Assert.Contains(h.Space.Graphs, g => g.Name == "deploymentReport");
        var report = h.Definitions().AsArray().Single(m => m!["name"]!.GetValue<string>() == "acme.report")!["projections"]!.AsArray();
        Assert.Equal(["acme.report.deploymentReport acme.report.deploymentReport.kql kql", "acme.report.title acme.report.title.heading heading"],
            report.Select(p => $"{p!["projection"]} {p["file"]} {p["language"]}"));
    }

    [Theory]
    [InlineData("acme.report.deploymentReport.kql", "kql")]
    [InlineData("acme.report.title.heading", "heading")]
    [InlineData("acme.report.sattle", null)]
    [InlineData("notes.md", null)]
    public void The_language_of_a_file_is_that_of_the_source_that_claims_it(string file, string? language) => Assert.Equal(language, Both.Language(file));

    [Fact]
    public void A_file_two_sources_claim_is_an_error_that_names_the_file_once()
    {
        var both = new ProjectionSources(new KqlSource(), new HeadingSource(alsoKql: true));
        Assert.Equal("kql", both.Language("acme.report.deploymentReport.kql"));     // still a projection file, not a module text
        var res = new Catalog(both).Load(Files(), updatePins: true);
        var claimed = res.Diagnostics.Where(d => d.Code == "ProjectionFile" && d.IsError).ToList();
        var report = Assert.Single(claimed, d => Path.GetFileName(d.File) == "acme.report.deploymentReport.kql");
        Assert.Equal("acme.report.deploymentReport.kql is claimed by 2 projection languages: kql, heading", report.Message);
        Assert.DoesNotContain(claimed, d => Path.GetFileName(d.File) == "acme.report.title.heading");
    }

    [Fact]
    public void A_statement_the_language_does_not_know_is_reported_in_its_file()
    {
        var files = Files();
        files[^1] = files[^1] with { Text = "headline Deployment health\n" };
        var res = new Catalog(Both).Load(files, updatePins: true);
        Assert.Contains(res.Diagnostics, d => d.Code == "Heading" && Path.GetFileName(d.File) == "acme.report.title.heading");
    }
}

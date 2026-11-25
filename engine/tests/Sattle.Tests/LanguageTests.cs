using Sattle.Core;
using Sattle.Language;

namespace Sattle.Tests;

/// <summary>Step 3: the modules as gkql documents (`.module.gkql.json`). Lowering to the catalog graph, checks K1 to K11, typing judgments, release digests. The projections come from their gkql programs.</summary>
public class LanguageTests
{
    static Dictionary<string, string> Sources() => Catalog.ReadDirectory(Repo.Definitions).ToDictionary(f => Path.GetFileName(f.Path), f => f.Text);

    static (Catalog Cat, LoadResult Result) Load(Dictionary<string, string> sources, bool updatePins = true)
    {
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(sources.Select(kv => new SourceFile(kv.Key, kv.Value)), updatePins);
        return (cat, res);
    }

    static (Catalog Cat, LoadResult Result) Mutated(string file, string from, string to)
    {
        var s = Sources();
        Assert.Contains(from, s[file]);
        s[file] = s[file].Replace(from, to);
        return Load(s);
    }

    [Fact]
    public void Examples_parse_check_and_activate_cleanly()
    {
        var (cat, res) = Load(Sources());
        Assert.Empty(res.Diagnostics);
        Assert.Equal(14, cat.Modules.Count);                               // the kernel and the thirteen modules of the definitions
        Assert.Equal(6, cat.Projections.Count);
        Assert.Single(cat.Spaces);
        Assert.All(cat.Modules.Values, m => Assert.StartsWith("sha256:", m.Digest));
    }

    [Fact]
    public void The_catalog_conforms_to_the_kernel_and_passes_K1_to_K11()
    {
        var (cat, _) = Load(Sources());
        Assert.Empty(Conformance.Check(cat.Graph, OntologyView.Build(Kernel.Graph, [Ids.KernelModule])));
        Assert.Empty(KernelChecks.Run(cat.Graph));
    }

    [Fact]
    public void Release_digests_are_reproducible_and_content_sensitive()
    {
        var (a, _) = Load(Sources());
        var (b, _) = Load(Sources());
        Assert.All(a.Modules.Keys, k => Assert.Equal(a.Modules[k].Digest, b.Modules[k].Digest));
        var (c, _) = Mutated("sattle.infra.module.gkql.json", "{\"name\": \"Instance\", \"properties\": [", "{\"name\": \"Instance\", \"properties\": [{\"name\": \"zone\", \"type\": \"Text\", \"optional\": true}, ");
        Assert.NotEqual(a.Modules["sattle.infra"].Digest, c.Modules["sattle.infra"].Digest);
    }

    [Fact]
    public void Import_pins_are_verified_and_can_be_fixed()
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "\"sha256:[0-9a-f]{64}\"", "null") }).ToList();
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: false);
        Assert.Contains(res.Diagnostics, d => d.Code == "ImportPinMissing" && d.Line > 0);
        res = new Catalog(Repo.Projections).Load(files, updatePins: true);
        Assert.NotEmpty(res.PinFixes);
        var fixedText = Catalog.ApplyPinFixes(files, res.PinFixes, Repo.Projections);
        var fixedFiles = files.Select(f => fixedText.TryGetValue(f.Path, out var t) ? f with { Text = t } : f).ToList();
        var again = new Catalog(Repo.Projections).Load(fixedFiles, updatePins: false);
        Assert.Empty(again.Diagnostics);
        var wrong = fixedFiles.Select(f => f.Path.EndsWith("acme.svc.module.gkql.json") ? f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "sha256:[0-9a-f]{64}", "sha256:" + new string('0', 64)) } : f).ToList();
        Assert.Contains(new Catalog(Repo.Projections).Load(wrong, updatePins: false).Diagnostics, d => d.Code is "ImportPinMismatch");
    }

    const string FlowsOut = "{\"name\": \"flowsOut\", \"type\": \"Integer\", \"optional\": true, \"events\": {\"temporality\": \"delta\", \"combine\": \"sum\", \"period\": \"1m\"}}";
    const string Flows = "{\"name\": \"flows\", \"type\": \"Integer\", \"optional\": true, \"events\": {\"temporality\": \"delta\", \"combine\": \"sum\", \"period\": \"1m\"}}";

    public static IEnumerable<object[]> Negative() =>
    [
        ["combine provenance mismatch", "acme.svc.module.gkql.json", FlowsOut, FlowsOut.Replace("\"combine\": \"sum\"", "\"combine\": \"max\""), "Set"],
        ["syntax error", "sattle.infra.module.gkql.json", "\"module\": \"sattle.infra\",", "\"module\": \"sattle.infra\"", "Syntax"],
        ["property declared twice with different kinds", "sattle.infra.module.gkql.json", "{\"name\": \"Service\", \"properties\": [{\"name\": \"site\", \"type\": \"Text\"}", "{\"name\": \"Service\", \"properties\": [{\"name\": \"site\", \"type\": \"Integer\"}", "PropertyDisagrees"],
        ["cumulative events cannot sum", "sattle.infra.module.gkql.json", Flows, Flows.Replace("\"delta\"", "\"cumulative\""), "K11"],
        ["a property written before #166 (field telemetry) is refused", "sattle.infra.module.gkql.json", Flows, Flows.Replace("\"events\"", "\"telemetry\""), "Syntax"],
    ];

    [Theory, MemberData(nameof(Negative))]
    public void Faulty_definitions_report_typed_errors_with_positions(string name, string file, string from, string to, string code)
    {
        _ = name;
        var (_, res) = Mutated(file, from, to);
        var hit = res.Diagnostics.FirstOrDefault(d => d.Code == code && d.IsError);
        Assert.True(hit is not null, string.Join("\n", res.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(hit!.File);
        Assert.True(hit.Line > 0, hit.ToString());
    }

    [Fact]
    public void Import_cycles_are_rejected()
    {
        var (_, res) = Load(new Dictionary<string, string>
        {
            ["a.module.gkql.json"] = """{"module": "a", "version": "1.0.0", "imports": [{"module": "b", "alias": "b", "version": "1.0.0", "digest": null}]}""",
            ["b.module.gkql.json"] = """{"module": "b", "version": "1.0.0", "imports": [{"module": "a", "alias": "a", "version": "1.0.0", "digest": null}]}""",
        });
        Assert.Contains(res.Diagnostics, d => d.Code == "K5" && d.Message.Contains("cycle"));
    }

    [Fact]
    public void Dependents_of_a_broken_module_are_not_activated()
    {
        var (cat, res) = Mutated("sattle.infra.module.gkql.json", "{\"name\": \"Backs\", \"from\": \"Instance\"", "{\"name\": \"Backs\", \"from\": \"Podd\"");
        Assert.Contains(res.Diagnostics, d => d.Code == "UnknownReference");
        Assert.DoesNotContain("acme.ops", cat.Modules.Keys);
        Assert.Empty(cat.Spaces);
    }

    // K1 to K11 on graphs: break one fact of a valid module and expect the rule to fire.
    static object[] KB(string code, Func<Graph, string, List<Edit>> f, string module) => [code, f, module];
    public static IEnumerable<object[]> KernelBreaks() =>
    [
        KB("K1", (g, m) => { var c = g.Rel(Ids.Rel(Ids.Module(m), "contains", Ids.Child(Ids.Module(m), "entity", "Instance")))!; return [Edit.Remove(c)]; }, "sattle.infra"),
        KB("K9", (g, m) => { var p = g.Entities.First(e => e.Id.StartsWith(Ids.Child(Ids.Module(m), "projection", "serviceDeps") + "/op:", StringComparison.Ordinal) && e.Id.EndsWith("/port:in", StringComparison.Ordinal)); return [Edit.Remove(g.Rel(Ids.Rel(p.Id[..p.Id.LastIndexOf("/port:", StringComparison.Ordinal)], "contains", p.Id))!), Edit.Remove(p)]; }, "acme.svc"),
        KB("K11", (g, m) => { var p = g.Entity(Ids.Child(Ids.Module(m), "prop", "flows"))!; return [Edit.Change(p, p with { Props = p.Props.With(Ids.KProp("period"), Value.Text("x")).Without(Ids.KProp("combine")) })]; }, "sattle.infra"),
    ];

    [Theory, MemberData(nameof(KernelBreaks))]
    public void Kernel_checks_fire_on_broken_graphs(string code, Func<Graph, string, List<Edit>> breaker, string module)
    {
        var (cat, _) = Load(Sources());
        var g = cat.Graph.Clone();
        Assert.Null(g.Apply(breaker(g, module)));
        var diags = KernelChecks.Run(g).Concat(Conformance.Check(g, OntologyView.Build(Kernel.Graph, [Ids.KernelModule])));
        Assert.Contains(diags, d => d.Code == code || (code == "K11" && d.Code == "ValueKind"));
    }

    [Theory]
    [InlineData("\"kind\": \"dependency\"", false)]
    [InlineData("\"kind\": \"dependency\", \"rollup\": true", true)]
    [InlineData("\"kind\": \"composition\", \"hierarchy\": true, \"rollup\": true", true)]
    public void The_rollup_marker_says_a_relationship_type_restates_others(string marks, bool rollup)
    {
        var (cat, res) = Load(new Dictionary<string, string>
        {
            ["marks.module.gkql.json"] = $$"""
                {"module": "marks", "version": "1.0.0", "imports": [],
                 "entities": [{"name": "Element", "properties": [{"name": "name", "type": "Text", "label": true}]}],
                 "relationships": [{"name": "Rolls", "from": "Element", "to": "Element", {{marks}} }]}
                """,
        });
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        Assert.Equal(rollup, new DefView(cat.Graph).Bool(Ids.Child(Ids.Module("marks"), "rel", "Rolls"), "rollup"));
    }

    [Fact]
    public void Ids_follow_the_containment_path_and_carry_no_version()
    {
        var (cat, _) = Load(Sources());
        var d = new DefView(cat.Graph);
        var proj = Ids.Child(Ids.Module("acme.svc"), "projection", "serviceDeps");
        Assert.Equal(Ids.Module("acme.svc"), d.Container(proj));
        Assert.NotNull(cat.Graph.Entity(proj + "/op:services_1_1/node:s"));
        Assert.NotNull(cat.Graph.Entity(proj + "/op:emit_5_1/source:#0"));
        Assert.Equal(proj + "/op:services_1_1", d.Container(proj + "/op:services_1_1/node:s"));
        Assert.All(cat.Graph.Entities.Where(e => d.TypeName(e.Id) != "Release"), e => Assert.DoesNotContain('@', e.Id));
    }
}

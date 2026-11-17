using Sattle.Core;
using Sattle.Language;

namespace Sattle.Tests;

/// <summary>Step 3: the .sattle front end of the modules. Parser, lowering to the catalog graph, canonical printer, checks K1 to K11, typing judgments, release digests. The projections come from their `.kql` files.</summary>
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
        Assert.Equal(18, cat.Modules.Count);                               // the kernel and the seventeen modules of the definitions
        Assert.Equal(13, cat.Projections.Count);
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
        var (c, _) = Mutated("sattle.infra.sattle", "phase?: Text; ready?: Boolean; serviceAccount?: Text;\n", "phase?: Text; ready?: Boolean; serviceAccount?: Text; zone?: Text;\n");
        Assert.NotEqual(a.Modules["sattle.infra"].Digest, c.Modules["sattle.infra"].Digest);
    }

    [Fact]
    public void Import_pins_are_verified_and_can_be_fixed()
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "sha256:[0-9a-f]{64}", "sha256:…") }).ToList();
        var cat = new Catalog(Repo.Projections);
        var res = cat.Load(files, updatePins: false);
        Assert.Contains(res.Diagnostics, d => d.Code == "ImportPinMissing" && d.Line > 0);
        res = new Catalog(Repo.Projections).Load(files, updatePins: true);
        Assert.NotEmpty(res.PinFixes);
        var fixedText = Catalog.ApplyPinFixes(files, res.PinFixes);
        var fixedFiles = files.Select(f => fixedText.TryGetValue(f.Path, out var t) ? f with { Text = t } : f).ToList();
        var again = new Catalog(Repo.Projections).Load(fixedFiles, updatePins: false);
        Assert.Empty(again.Diagnostics);
        var wrong = fixedFiles.Select(f => f.Path.EndsWith("acme.svc.sattle") ? f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "sha256:[0-9a-f]{64}", "sha256:" + new string('0', 64)) } : f).ToList();
        Assert.Contains(new Catalog(Repo.Projections).Load(wrong, updatePins: false).Diagnostics, d => d.Code is "ImportPinMismatch");
    }

    [Fact]
    public void Print_then_parse_reproduces_the_graph_and_printing_is_idempotent()
    {
        var sources = Sources();
        var (cat, _) = Load(sources);
        var printed = Printed(cat);
        var (cat2, res2) = Load(printed.Concat(sources.Where(kv => kv.Key.EndsWith(".kql", StringComparison.Ordinal))).ToDictionary(), updatePins: false);
        Assert.Empty(res2.Diagnostics);
        foreach (var m in cat.Modules.Keys) Assert.Equal(cat.Modules[m].Digest, cat2.Modules[m].Digest); // equal digests = equal module facts
        Assert.Equal(printed, Printed(cat2));
    }

    /// <summary>Each module of a catalog printed: its text, without the projections its `.kql` files give.</summary>
    static Dictionary<string, string> Printed(Catalog cat) =>
        cat.Modules.Keys.Where(m => m != Ids.KernelModule).ToDictionary(m => m + ".sattle", m => Printer.Print(cat.Graph, m));

    public static IEnumerable<object[]> Negative() =>
    [
        ["combine provenance mismatch", "acme.svc.sattle", "flowsOut?: Integer telemetry delta sum every 60s;", "flowsOut?: Integer telemetry delta max every 60s;", "Set"],
        ["syntax error", "sattle.infra.sattle", "module sattle.infra @ 1.0.0;", "module sattle.infra @ 1.0.0", "Syntax"],
        ["property declared twice with different kinds", "sattle.infra.sattle", "entity Service { cluster: Text;", "entity Service { cluster: Integer;", "PropertyDisagrees"],
        ["constraint on a non-text property", "sattle.infra.sattle", "constraint enum Pod.phase", "constraint enum Pod.nonexistent", "UnknownProperty"],
        ["cumulative telemetry cannot sum", "sattle.infra.sattle", "flows?: Integer telemetry delta sum every 60s;", "flows?: Integer telemetry cumulative sum every 60s;", "K11"],
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
            ["a.sattle"] = "module a @ 1.0.0;\nimport b @ 1.0.0 sha256:… as b;\n",
            ["b.sattle"] = "module b @ 1.0.0;\nimport a @ 1.0.0 sha256:… as a;\n",
        });
        Assert.Contains(res.Diagnostics, d => d.Code == "K5" && d.Message.Contains("cycle"));
    }

    [Fact]
    public void Dependents_of_a_broken_module_are_not_activated()
    {
        var (cat, res) = Mutated("sattle.infra.sattle", "relationship Backs (Pod)", "relationship Backs (Podd)");
        Assert.Contains(res.Diagnostics, d => d.Code == "UnknownReference");
        Assert.DoesNotContain("acme.ops", cat.Modules.Keys);
        Assert.Empty(cat.Spaces);
    }

    // K1 to K11 on graphs: break one fact of a valid module and expect the rule to fire.
    static object[] KB(string code, Func<Graph, string, List<Edit>> f, string module) => [code, f, module];
    public static IEnumerable<object[]> KernelBreaks() =>
    [
        KB("K1", (g, m) => { var c = g.Rel(Ids.Rel(Ids.Module(m), "contains", Ids.Child(Ids.Module(m), "entity", "Pod")))!; return [Edit.Remove(c)]; }, "sattle.infra"),
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
    [InlineData("kind dependency;", false)]
    [InlineData("rollup kind dependency;", true)]
    [InlineData("hierarchy rollup kind composition;", true)]
    public void The_rollup_marker_says_a_relationship_type_restates_others_and_prints_back(string tail, bool rollup)
    {
        var (cat, res) = Mutated("acme.tree.sattle", "relationship Rolls (Element) -> (Element) kind dependency;", "relationship Rolls (Element) -> (Element) " + tail);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
        var rolls = Ids.Child(Ids.Module("acme.tree"), "rel", "Rolls");
        Assert.Equal(rollup, new DefView(cat.Graph).Bool(rolls, "rollup"));
        Assert.Contains("relationship Rolls (Element) -> (Element) " + tail.TrimEnd(';'), Printer.Print(cat.Graph, "acme.tree"));
    }

    [Fact]
    public void Was_clause_keeps_the_old_id()
    {
        var s = Sources();
        s["sattle.infra.sattle"] = s["sattle.infra.sattle"].Replace("entity Service {", "entity Svc was Service {");
        var (cat, res) = Load(s);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError && d.File == "sattle.infra.sattle");     // a KQL text names a type by its name, so the projections that read Service would need the new name
        var node = cat.Graph.Entity(Ids.Child(Ids.Module("sattle.infra"), "entity", "Service"));
        Assert.Equal("Svc", node!.Props.Text(Ids.KProp("name")));
        Assert.Contains("entity Svc was Service", Printer.Print(cat.Graph, "sattle.infra"));
    }

    [Fact]
    public void Ids_follow_the_containment_path_and_carry_no_version()
    {
        var (cat, _) = Load(Sources());
        var d = new DefView(cat.Graph);
        var proj = Ids.Child(Ids.Module("acme.svc"), "projection", "serviceDeps");
        Assert.Equal(Ids.Module("acme.svc"), d.Container(proj));
        Assert.NotNull(cat.Graph.Entity(proj + "/op:services_1/node:s"));
        Assert.NotNull(cat.Graph.Entity(proj + "/op:emit.5/source:#0"));
        Assert.Equal(proj + "/op:services_1", d.Container(proj + "/op:services_1/node:s"));
        Assert.All(cat.Graph.Entities.Where(e => d.TypeName(e.Id) != "Release"), e => Assert.DoesNotContain('@', e.Id));
    }
}

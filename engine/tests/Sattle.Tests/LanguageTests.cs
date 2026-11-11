using Sattle.Core;
using Sattle.Language;

namespace Sattle.Tests;

/// <summary>Step 3: the .sattle front end. Parser, lowering to the catalog graph, canonical printer, checks K1 to K11, typing judgments, release digests.</summary>
public class LanguageTests
{
    static Dictionary<string, string> Sources() => Catalog.ReadDirectory(Repo.Definitions).ToDictionary(f => Path.GetFileName(f.Path), f => f.Text);

    static (Catalog Cat, LoadResult Result) Load(Dictionary<string, string> sources, bool updatePins = true)
    {
        var cat = new Catalog();
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
        Assert.Equal(19, cat.Modules.Count);
        Assert.Equal(14, cat.Projections.Count);
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
        var (c, _) = Mutated("sattle.infra.sattle", "phase?: Text; ready?: Boolean;\n", "phase?: Text; ready?: Boolean; zone?: Text;\n");
        Assert.NotEqual(a.Modules["sattle.infra"].Digest, c.Modules["sattle.infra"].Digest);
    }

    [Fact]
    public void Import_pins_are_verified_and_can_be_fixed()
    {
        var files = Catalog.ReadDirectory(Repo.Definitions).Select(f => f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "sha256:[0-9a-f]{64}", "sha256:…") }).ToList();
        var cat = new Catalog();
        var res = cat.Load(files, updatePins: false);
        Assert.Contains(res.Diagnostics, d => d.Code == "ImportPinMissing" && d.Line > 0);
        res = new Catalog().Load(files, updatePins: true);
        Assert.NotEmpty(res.PinFixes);
        var fixedText = Catalog.ApplyPinFixes(files, res.PinFixes);
        var fixedFiles = files.Select(f => fixedText.TryGetValue(f.Path, out var t) ? f with { Text = t } : f).ToList();
        var again = new Catalog().Load(fixedFiles, updatePins: false);
        Assert.Empty(again.Diagnostics);
        var wrong = fixedFiles.Select(f => f.Path.EndsWith("acme.svc.sattle") ? f with { Text = System.Text.RegularExpressions.Regex.Replace(f.Text, "sha256:[0-9a-f]{64}", "sha256:" + new string('0', 64)) } : f).ToList();
        Assert.Contains(new Catalog().Load(wrong, updatePins: false).Diagnostics, d => d.Code is "ImportPinMismatch");
    }

    [Fact]
    public void Print_then_parse_reproduces_the_graph_and_printing_is_idempotent()
    {
        var (cat, _) = Load(Sources());
        var printed = cat.Modules.Keys.Where(m => m != Ids.KernelModule).ToDictionary(m => m + ".sattle", m => Printer.Print(cat.Graph, m));
        var (cat2, res2) = Load(printed, updatePins: false);
        Assert.Empty(res2.Diagnostics);
        foreach (var m in cat.Modules.Keys) Assert.Equal(cat.Modules[m].Digest, cat2.Modules[m].Digest); // equal digests = equal module facts
        foreach (var m in printed.Keys) Assert.Equal(printed[m], Printer.Print(cat2.Graph, Path.GetFileNameWithoutExtension(m)));
    }

    public static IEnumerable<object[]> Negative() =>
    [
        ["unknown field", "acme.svc.sattle", "drops / flows", "drops / nosuch", "UnknownField"],
        ["unknown function", "acme.ops.sattle", "isPresent(srcPod)", "isMissing(srcPod)", "UnknownFunction"],
        ["unknown alias", "acme.ops.sattle", "emit entity infra.Pod", "emit entity nope.Pod", "UnknownAlias"],
        ["unknown identity component", "acme.ops.sattle", "uid: uid)\n      set", "ui: uid)\n      set", "UnknownComponent"],
        ["wrong value kind for a property", "acme.ops.sattle", "set { flows: flows, drops: drops }", "set { flows: srcPod, drops: drops }", "Set"],
        ["required property missing on create", "acme.ops.sattle", "set { cluster: cluster, namespace: namespace, name: name };\n    // absent", "set { cluster: cluster, namespace: namespace };\n    // absent", "P5"],
        ["bijective rule over a lossy sink", "acme.ops.sattle", "rule observedPods readonly", "rule observedPods bijective", "DirectionOverclaim"],
        ["combine provenance mismatch", "acme.svc.sattle", "flowsOut?: Integer telemetry delta sum every 60s;", "flowsOut?: Integer telemetry delta max every 60s;", "Set"],
        ["telemetry into graph data", "acme.svc.sattle", "set { flowsOut: flowsOut };\n  let groupOut", "set { pods: flowsOut };\n  let groupOut", "RoleInterference"],
        ["threshold bounds", "acme.svc.sattle", "above 0.05 for 3", "above 0.01 for 3", "Threshold"],
        ["window needs a data Instant", "acme.ops.sattle", "window on time", "window on verdict", "Window"],
        ["period mismatch between row window and property", "acme.ops.sattle", "window on time width 60s", "window on time width 30s", "Set"],
        ["syntax error", "acme.ops.sattle", "retain 8;", "retain 8", "Syntax"],
        ["unsupported construct is reported with a position", "acme.svc.sattle", "|> rollup by { s: src } combine { flowsOut: sum(flows) };", "|> traverse;", "Syntax"],
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
        KB("K9", (g, m) => { var p = g.Entity(Ids.Child(Ids.Child(Ids.Child(Ids.Module(m), "projection", "serviceDeps"), "op", "serviceOut"), "port", "in"))!; return [Edit.Remove(g.Rel(Ids.Rel(Ids.Child(Ids.Child(Ids.Module(m), "projection", "serviceDeps"), "op", "serviceOut"), "contains", p.Id))!), Edit.Remove(p)]; }, "acme.svc"),
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

    [Fact]
    public void Was_clause_keeps_the_old_id()
    {
        var s = Sources();
        s["sattle.infra.sattle"] = s["sattle.infra.sattle"].Replace("entity Service {", "entity Svc was Service {");
        var (cat, res) = Load(s);
        Assert.DoesNotContain(res.Diagnostics, d => d.IsError);
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
        Assert.NotNull(cat.Graph.Entity(proj + "/op:services/node:s"));
        Assert.NotNull(cat.Graph.Entity(proj + "/op:emit.5/source:#0"));
        Assert.Equal(proj + "/op:services", d.Container(proj + "/op:services/node:s"));
        Assert.All(cat.Graph.Entities.Where(e => d.TypeName(e.Id) != "Release"), e => Assert.DoesNotContain('@', e.Id));
    }
}

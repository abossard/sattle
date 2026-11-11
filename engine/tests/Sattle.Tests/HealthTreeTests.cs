using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The health tree of the whole estate (README, "Health tree of the estate"): derived from the master with no names in the definitions, signals and roll-up policies and curation are authored records, and a record changes the result without a definition change.</summary>
public class HealthTreeTests
{
    static string Fixtures => Path.Combine(Repo.Root, "engine", "tests", "Sattle.Tests", "fixtures");
    static string Wide => Path.Combine(Fixtures, "estate-wide");
    const string Cluster = "synth";

    /// <summary>A copy of a capture directory, optionally without some authoring files, with authoring records added (one JSON payload per line, kind inside).</summary>
    static string Variant(string source, string[]? without = null, string[]? records = null, Func<string, string>? editRules = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sattle-tree-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f);
            if (rel.StartsWith("space", StringComparison.Ordinal) || (without ?? []).Any(w => rel.Replace('\\', '/') == w)) continue;
            var target = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (editRules is not null && rel.Replace('\\', '/') == "authoring/rules.ndjson") File.WriteAllText(target, editRules(File.ReadAllText(f))); else File.Copy(f, target);
        }
        if (records is { Length: > 0 })
        {
            Directory.CreateDirectory(Path.Combine(dir, "authoring"));
            File.AppendAllLines(Path.Combine(dir, "authoring", "extra.ndjson"), records.Select((r, i) => $"{{\"v\":1,\"source\":\"authoring\",\"capturedAt\":\"2026-01-01T10:00:00.900Z\",\"seq\":{i + 1},\"key\":\"authoring/extra/{i}\",\"meta\":null,\"payload\":{r}}}"));
        }
        return dir;
    }

    static Engine Run(string dir, int chunk = 2000) => ReplayTests.Replay(dir, chunk, Cluster, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 }).Engine;

    static string P(Entity e, string prop) => e.Props.TryGet("d:acme.tree/prop:" + prop, out var v) ? v switch { TextValue t => t.S, IntegerValue i => i.N.ToString(), BooleanValue b => b.B ? "true" : "false", _ => v.ToString()! } : "";
    static Dictionary<string, Entity> Elements(Engine e, string graph = "healthTree") => e.Graph(graph).State.Entities.Where(x => Ids.Name(x.Type) == "Element").ToDictionary(x => P(x, "nodeKey"));
    static Dictionary<string, string> Parents(Engine e, string graph = "healthTree")
    {
        var g = e.Graph(graph).State; var byId = g.Entities.Where(x => Ids.Name(x.Type) == "Element").ToDictionary(x => x.Id, x => P(x, "nodeKey"));
        return g.Rels.Where(r => Ids.Name(r.Type) == "Contains").ToDictionary(r => byId[r.Target], r => byId[r.Source]);
    }
    static string State(Engine e, string key) => P(Elements(e)[key], "rolledState");

    // ---- the tree ----------------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Captures => new() { Path.Combine(Fixtures, "estate"), Wide, "wide-without-curation", "wide-without-authoring" };
    static string Resolve(string name) => name switch
    {
        "wide-without-curation" => Variant(Wide, without: ["authoring/curation.ndjson"]),
        "wide-without-authoring" => Variant(Wide, without: ["authoring/curation.ndjson", "authoring/rules.ndjson"]),
        _ => name,
    };

    [Theory, MemberData(nameof(Captures))]
    public void Every_element_has_exactly_one_parent_the_hierarchy_is_acyclic_and_every_element_reaches_a_root(string capture)
    {
        var dir = Resolve(capture);
        var e = Run(dir); var elements = Elements(e, "treeStructure"); var parents = Parents(e, "treeStructure");
        Assert.NotEmpty(elements);
        Assert.Equal(parents.Count, e.Graph("treeStructure").State.Rels.Count(r => Ids.Name(r.Type) == "Contains"));              // one Contains per child: no element has two parents
        var roots = elements.Keys.Where(k => !parents.ContainsKey(k)).ToList();
        Assert.NotEmpty(roots);
        foreach (var k in elements.Keys)
        {
            var cur = k; var steps = 0;
            while (parents.TryGetValue(cur, out var up)) { cur = up; Assert.True(++steps <= 12, $"{k}: the parent chain is longer than the depth of the tree (a cycle?)"); }
            Assert.Contains(cur, roots);
        }
        var cat = Repo.LoadExamples(out _);
        var violations = Conformance.Check(e.Graph("treeStructure").State, cat.OntologyOf(e.Graph("treeStructure").Typed.OutputModule));
        Assert.DoesNotContain(violations, d => d.Code is "Cardinality" or "Acyclic");
    }

    [Fact]
    public void A_cluster_is_one_element_the_Azure_resource_with_the_Kubernetes_subtree_below_it()
    {
        var dir = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var e = Run(dir); var els = Elements(e); var parents = Parents(e);
            var clusters = els.Values.Where(x => P(x, "nodeKind") == "cluster").Select(x => P(x, "nodeKey")).ToList();
            Assert.Equal(2, clusters.Count);
            Assert.All(clusters, k => { Assert.StartsWith("/subscriptions/", k); Assert.StartsWith("rg/", parents[k]); });                // below its resource group, like any resource
            Assert.Equal(clusters.Select(k => k).Order(), clusters.Select(k => parents["ns/" + (k.Contains("aks-red") ? "red" : "blue") + "/" + (k.Contains("aks-red") ? "shop" : "web")]).Order());   // a namespace is below the same element
            Assert.DoesNotContain(els.Keys, k => k.StartsWith("cluster/", StringComparison.Ordinal));                                      // no second cluster element
            Assert.Equal(3, els.Values.Count(x => P(x, "nodeKind") == "subscription")); Assert.Equal(8, els.Values.Count(x => P(x, "nodeKind") == "resourceGroup"));
            Assert.Equal(6, els.Values.Count(x => P(x, "nodeKind") == "namespace")); Assert.Equal(12, els.Values.Count(x => P(x, "nodeKind") == "workload")); Assert.Equal(24, els.Values.Count(x => P(x, "nodeKind") == "pod"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Without_any_authored_record_the_full_default_tree_comes_out_and_the_authored_ones_only_add()
    {
        var bare = Variant(Wide, without: ["authoring/curation.ndjson", "authoring/rules.ndjson"]); var full = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var a = Run(bare); var b = Run(full);
            Assert.Equal(Elements(b).Keys.Order(), Elements(a).Keys.Order());
            Assert.Equal(Parents(b).OrderBy(x => x.Key), Parents(a).OrderBy(x => x.Key));
            Assert.All(Elements(a).Values, x => Assert.Equal("Unknown", P(x, "rolledState")));                                                   // no rule, no signal: nothing is known to be wrong
            Assert.Contains(Elements(b).Values, x => P(x, "rolledState") == "Unhealthy");
        }
        finally { Directory.Delete(bare, true); Directory.Delete(full, true); }
    }

    // ---- roll-up ------------------------------------------------------------------------------------------------------------------------

    static string OnlyPodRules(string t) => string.Join('\n', t.Split('\n').Where(l => l.Contains("pod-warnings", StringComparison.Ordinal)));
    static readonly string[] Chain = ["pod/red/pay/ledger-7a1-1", "wl/red/pay/ledger", "ns/red/pay"];

    [Theory]
    [InlineData("worst", "", "Unhealthy,Unhealthy,Unhealthy", "Unhealthy")]                                                                                                 // the default: every element above the pod is Unhealthy, up to the subscription
    [InlineData("ignore on the namespace", "{\"kind\":\"policy\",\"id\":\"ns/red/pay\",\"mode\":\"ignore\"}", "Unhealthy,Unhealthy,Unknown", "Unknown")]                  // pay keeps its own state: what is below it does not reach it or its ancestors
    [InlineData("share of 2 for workloads", "{\"kind\":\"policy\",\"id\":\"workload\",\"mode\":\"share\",\"share\":2.0}", "Unhealthy,Degraded,Unhealthy", "Unhealthy")]   // a workload is Unhealthy only when more than twice as many ... never: Degraded
    public void A_failure_on_a_pod_rolls_up_through_three_levels_by_the_policy_of_each_element(string policy, string record, string states, string subscription)
    {
        _ = policy;
        var dir = Variant(Wide, without: ["authoring/curation.ndjson"], records: record == "" ? null : [record], editRules: OnlyPodRules);       // only the pod rule: the cluster CPU and the node drops would hide the roll-up
        try
        {
            var e = Run(dir);
            Assert.Equal(states.Split(','), Chain.Select(k => State(e, k)).ToArray());
            Assert.Equal(subscription, State(e, "sub/sub-a"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_counts_of_an_element_are_the_monitored_elements_at_and_below_it_per_state()
    {
        var dir = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var e = Run(dir); var els = Elements(e);
            int N(string k, string p) => int.Parse(P(els[k], p));
            Assert.Equal(N("sub/sub-b", "monitored"), N("sub/sub-b", "healthyCount") + N("sub/sub-b", "degradedCount") + N("sub/sub-b", "unhealthyCount"));
            Assert.Equal(1, N("pod/red/pay/ledger-7a1-1", "unhealthyCount"));
            Assert.True(N("sub/sub-a", "unhealthyCount") >= N("ns/red/pay", "unhealthyCount"));
            Assert.All(els.Values, x => Assert.Equal(int.Parse(P(x, "monitored")), int.Parse(P(x, "healthyCount")) + int.Parse(P(x, "degradedCount")) + int.Parse(P(x, "unhealthyCount"))));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- curation: each kind of record, and its removal ---------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"kind\":\"curation\",\"id\":\"m1\",\"action\":\"move\",\"target\":\"ns/blue/mon\",\"value\":\"sub/sub-c\"}", "ns/blue/mon", "sub/sub-c")]           // move: another parent
    [InlineData("{\"kind\":\"curation\",\"id\":\"g1\",\"action\":\"group\",\"target\":\"rg/sub-b/rg-data\",\"value\":\"Data estate\"}", "rg/sub-b/rg-data", "grp/Data estate")]   // group: a named workload takes it
    public void A_move_or_group_record_changes_the_parent_and_removing_it_restores_the_default(string record, string target, string parent)
    {
        var with = Variant(Wide, without: ["authoring/curation.ndjson"], records: [record]); var without = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var a = Run(with); var b = Run(without);
            Assert.Equal(parent, Parents(a)[target]);
            Assert.NotEqual(parent, Parents(b)[target]);
            if (parent.StartsWith("grp/", StringComparison.Ordinal)) { Assert.Equal("workload", P(Elements(a)[parent], "nodeKind")); Assert.DoesNotContain(parent, Elements(b).Keys); }
            Assert.Equal(Elements(b).Count + (parent.StartsWith("grp/", StringComparison.Ordinal) ? 1 : 0), Elements(a).Count);
        }
        finally { Directory.Delete(with, true); Directory.Delete(without, true); }
    }

    [Theory]
    [InlineData("hide", "{\"kind\":\"curation\",\"id\":\"h1\",\"action\":\"hide\",\"target\":\"ns/red/pay\"}")]
    [InlineData("critical", "{\"kind\":\"curation\",\"id\":\"c9\",\"action\":\"critical\",\"target\":\"wl/blue/web/site\"}")]
    public void A_hide_record_takes_a_subtree_out_of_the_roll_up_and_a_critical_record_marks_an_element_and_removing_either_restores_the_default(string action, string record)
    {
        var with = Variant(Wide, without: ["authoring/curation.ndjson"], records: [record]); var without = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var a = Run(with); var b = Run(without);
            if (action == "hide")
            {
                Assert.Equal("true", P(Elements(a)["pod/red/pay/ledger-7a1-1"], "hidden")); Assert.Equal("", P(Elements(b)["pod/red/pay/ledger-7a1-1"], "hidden"));
                Assert.Equal("Unknown", State(a, "pod/red/pay/ledger-7a1-1")); Assert.Equal("Unhealthy", State(b, "pod/red/pay/ledger-7a1-1"));
                Assert.Equal("0", P(Elements(a)["ns/red/pay"], "monitored")); Assert.NotEqual("0", P(Elements(b)["ns/red/pay"], "monitored"));
            }
            else
            {
                Assert.Equal("true", P(Elements(a)["wl/blue/web/site"], "critical")); Assert.Equal("", P(Elements(b)["wl/blue/web/site"], "critical"));
                Assert.Equal(State(b, "wl/blue/web/site"), State(a, "wl/blue/web/site"));                       // the mark is data, it changes no state
            }
        }
        finally { Directory.Delete(with, true); Directory.Delete(without, true); }
    }

    // ---- signals are data ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a new rule row", "{\"kind\":\"signalRule\",\"id\":\"cpu-warm\",\"type\":\"microsoft.containerservice/managedclusters\",\"meter\":\"node_cpu_usage_percentage\",\"above\":30,\"enterAfter\":2,\"below\":20,\"exitAfter\":2,\"severity\":1,\"invert\":false}", "/subscriptions/sub-b/resourcegroups/rg-blue/providers/microsoft.containerservice/managedclusters/aks-blue", "Healthy", "Degraded")]   // aks-blue runs at 35 %
    [InlineData("a changed row", "{\"kind\":\"signalRule\",\"id\":\"pod-warnings-critical\",\"type\":\"pod\",\"meter\":\"warnings\",\"above\":7,\"enterAfter\":1,\"below\":1,\"exitAfter\":3,\"severity\":2,\"invert\":false}", "pod/red/pay/ledger-7a1-1", "Unhealthy", "Degraded")]            // six events no longer reach 7
    [InlineData("an inverted meter", "{\"kind\":\"signalRule\",\"id\":\"cpu-low\",\"type\":\"microsoft.containerservice/managedclusters\",\"meter\":\"node_cpu_usage_percentage\",\"above\":-50,\"enterAfter\":2,\"below\":-60,\"exitAfter\":2,\"severity\":2,\"invert\":true}", "/subscriptions/sub-b/resourcegroups/rg-blue/providers/microsoft.containerservice/managedclusters/aks-blue", "Healthy", "Unhealthy")]   // a cluster below 50 % is flagged
    public void A_rule_record_added_or_changed_changes_the_signals_and_the_states_without_a_definition_change(string what, string record, string element, string before, string after)
    {
        _ = what;
        var plain = Variant(Wide, without: ["authoring/curation.ndjson"]); var changed = Variant(Wide, without: ["authoring/curation.ndjson"], records: [record]);
        try
        {
            var a = Run(plain); var b = Run(changed);
            Assert.Equal(before, P(Elements(a)[element], "ownState"));
            Assert.Equal(after, P(Elements(b)[element], "ownState"));
        }
        finally { Directory.Delete(plain, true); Directory.Delete(changed, true); }
    }

    [Fact]
    public void The_threshold_of_a_signal_rule_is_read_from_the_row_and_prints_back_as_the_expression_it_was_written_as()
    {
        var cat = Repo.LoadExamples(out _);
        var text = Printer.Print(cat.Graph, "acme.tree");
        Assert.Contains("|> threshold v by { ekey: ekey, rid: rid, severity: severity } above above for enterAfter then \"on\" below below for exitAfter then \"off\" initial \"off\" as state;", text);
    }

    // ---- the graph is a tree for a viewer ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_ontology_marks_the_hierarchy_relationship_and_gives_the_states_value_roles()
    {
        var run = ReplayTests.Replay(Wide, cluster: Cluster, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        var onto = run.Export.Ontology(run.Engine.Graph("healthTree"));
        var rels = onto["relationshipTypes"]!.AsArray().Where(x => x!["hierarchy"] is not null).ToDictionary(x => x!["id"]!["value"]!.GetValue<string>(), x => x!["hierarchy"]!.GetValue<bool>());
        bool Marked(string name) => rels.Single(kv => kv.Key == name || kv.Key.EndsWith("." + name, StringComparison.Ordinal) && kv.Key.StartsWith("acme.tree", StringComparison.Ordinal)).Value;
        Assert.True(Marked("Contains")); Assert.False(Marked("Rolls")); Assert.False(Marked("EvidenceFor"));
        var state = onto["properties"]!.AsArray().Single(p => p!["id"]!["value"]!.GetValue<string>() == "rolledState")!;
        Assert.Equal("Degraded:warning,Healthy:ok,Unhealthy:danger,Unknown:unknown", string.Join(",", state["presentation"]!.AsArray().Select(x => x!["value"]!["value"]!.GetValue<string>() + ":" + x["role"]!.GetValue<string>()).Order()));
    }

    [Fact]
    public void The_ontology_serves_treeState_per_entity_type_so_a_viewer_needs_no_property_names()
    {
        var run = ReplayTests.Replay(Wide, cluster: Cluster, o: new EngineOptions { Tenant = "lab", LateHorizonWindows = 1_000_000 });
        var onto = run.Export.Ontology(run.Engine.Graph("healthTree"));
        var element = onto["entityTypes"]!.AsArray().Single(x => x!["id"]!["value"]!.GetValue<string>() == "Element")!["treeState"]!;
        Assert.Equal("ownState", element["own"]!.GetValue<string>()); Assert.Equal("rolledState", element["rolledUp"]!.GetValue<string>());
        Assert.Equal("rolledSeverity", element["severity"]!.GetValue<string>()); Assert.Equal("nodeKind", element["kind"]!.GetValue<string>());
        Assert.Equal("healthyCount", element["counts"]!["Healthy"]!.GetValue<string>()); Assert.Equal("unhealthyCount", element["counts"]!["Unhealthy"]!.GetValue<string>());
        Assert.Equal(["critical", "curatedBy", "hidden", "mode", "share"], new[] { "critical", "curatedBy", "hidden", "mode", "share" }.Where(k => element[k] is not null).ToArray());
        Assert.Equal("rollupMode", element["mode"]!.GetValue<string>());
        Assert.Null(onto["entityTypes"]!.AsArray().Single(x => x!["id"]!["value"]!.GetValue<string>().EndsWith("Signal", StringComparison.Ordinal))!["treeState"]);
    }

    [Fact]
    public void A_signal_is_an_entity_of_the_tree_graph_with_its_rule_thresholds_and_a_streamed_value_and_measures_its_element()
    {
        var dir = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            var e = Run(dir); var g = e.Graph("healthTree").State;
            var signal = g.Entities.First(x => Ids.Name(x.Type) == "Signal" && P(x, "signalRule") == "pod-warnings" && P(x, "signalElement") == "pod/red/pay/ledger-7a1-1");
            Assert.Equal("on", P(signal, "signalState")); Assert.Equal("1", P(signal, "signalSeverity")); Assert.Equal("warnings", P(signal, "signalMeter"));
            Assert.Equal("1", P(signal, "signalEnterAfter")); Assert.Equal("3", P(signal, "signalExitAfter")); Assert.Equal("false", P(signal, "signalInverted"));
            Assert.Contains(g.Rels, r => Ids.Name(r.Type) == "Measures" && r.Source == signal.Id && P(g.Entity(r.Target)!, "nodeKey") == "pod/red/pay/ledger-7a1-1");
            Assert.Contains(((InMemoryTelemetryStore)e.Telemetry).Dump(e.Graph("healthTree").Key), x => e.Interner.Get(x.Element) == signal.Id && e.Interner.Get(x.Prop).EndsWith("prop:signalValue", StringComparison.Ordinal) && x.Value > 0);
            var curated = Run(Wide);
            Assert.Equal("c1", P(Elements(curated)["wl/red/pay/ledger"], "curatedBy"));
            Assert.Equal("worst", P(Elements(e)["ns/red/pay"], "rollupMode"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Evidence_hangs_on_the_element_it_supports_and_every_evidence_edge_has_both_ends_in_the_graph()
    {
        var e = Run(Wide); var g = e.Graph("healthTree").State;
        var ev = g.Rels.Where(r => Ids.Name(r.Type) == "EvidenceFor").ToList();
        Assert.NotEmpty(ev);
        Assert.All(ev, r => { Assert.NotNull(g.Entity(r.Source)); Assert.NotNull(g.Entity(r.Target)); Assert.Equal("Element", Ids.Name(g.Entity(r.Target)!.Type)); });
        Assert.DoesNotContain(g.Rels.Where(r => Ids.Name(r.Type) == "Contains"), r => Ids.Name(g.Entity(r.Source)!.Type) == "Evidence");    // evidence is never a child
    }

    [Fact]
    public void The_report_document_lists_the_totals_per_state_and_the_elements_that_are_not_healthy_worst_first()
    {
        var h = new SpaceHost(Repo.DefinitionFilesFor(Wide), new HostOptions { Cluster = Cluster, LateHorizonWindows = 1_000_000 });
        using (h)
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = Cluster }).Run(Wide);
            h.Finish();
            var md = h.Document("healthReport", null).Markdown;
            Assert.Contains("# Estate health", md); Assert.Contains("- Elements: 70", md);
            var (unhealthy, degraded) = (md.IndexOf("| subscription | sub\\-b | Unhealthy", StringComparison.Ordinal), md.IndexOf("| resource | hub\\-shared | Degraded", StringComparison.Ordinal));
            Assert.True(unhealthy >= 0 && degraded >= 0, "the report lacks the Unhealthy subscription or the Degraded resource");
            Assert.True(unhealthy < degraded, "the Unhealthy rows come before the Degraded ones");
        }
    }

    // ---- the engine properties --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)] [InlineData(50)] [InlineData(100000)]
    public void The_state_digest_of_the_health_tree_does_not_depend_on_the_chunk_size(int chunk)
    {
        var reference = Run(Wide, 2000); var e = Run(Wide, chunk);
        Assert.Equal(reference.StateDigest(), e.StateDigest());
        Assert.Equal(Elements(reference).OrderBy(x => x.Key).Select(x => P(x.Value, "rolledState")), Elements(e).OrderBy(x => x.Key).Select(x => P(x.Value, "rolledState")));
    }

    [Fact]
    public void A_pod_that_starts_to_fail_changes_only_its_path_to_the_root_and_a_few_edits()
    {
        var baseDir = Variant(Wide, without: ["authoring/curation.ndjson"]);
        var extra = Variant(Wide, without: ["authoring/curation.ndjson"]);
        try
        {
            // six warning events on a healthy pod of the other cluster, after everything else
            var lines = Enumerable.Range(0, 6).Select(i => $"{{\"v\":1,\"source\":\"kubernetes-watch\",\"capturedAt\":\"2026-01-01T10:08:{10 + i:00}.000Z\",\"seq\":{9000 + i},\"key\":\"kubernetes-watch/ADDED/Event/late-{i}/9\",\"meta\":{{\"path\":\"/synthetic\",\"cluster\":\"blue\"}},\"payload\":{{\"type\":\"ADDED\",\"object\":{{\"kind\":\"Event\",\"involvedObject\":{{\"kind\":\"Pod\",\"name\":\"etl-7a1-1\",\"namespace\":\"batch\"}},\"reason\":\"BackOff\",\"type\":\"Warning\",\"message\":\"late\",\"count\":1,\"lastTimestamp\":\"2026-01-01T10:08:{10 + i:00}.000Z\",\"metadata\":{{\"name\":\"etl-7a1-1.late{i}\",\"uid\":\"late-{i}\",\"namespace\":\"batch\",\"resourceVersion\":\"{9000 + i}\"}}}}}}}}");
            File.AppendAllLines(Path.Combine(extra, "kubernetes", "watch.ndjson"), lines);
            var a = Run(baseDir); var b = Run(extra);
            var before = Elements(a); var after = Elements(b);
            var changed = after.Keys.Where(k => P(before[k], "rolledState") != P(after[k], "rolledState") || P(before[k], "rolledSeverity") != P(after[k], "rolledSeverity") || P(before[k], "unhealthyCount") != P(after[k], "unhealthyCount") || P(before[k], "degradedCount") != P(after[k], "degradedCount") || P(before[k], "monitored") != P(after[k], "monitored")).Order().ToList();
            var path = new List<string> { "pod/blue/batch/etl-7a1-1" }; var parents = Parents(b);
            while (parents.TryGetValue(path[^1], out var up)) path.Add(up);
            Assert.NotEmpty(changed);
            Assert.All(changed, k => Assert.Contains(k, path));                                              // nothing outside the pod's path to the root changed
            Assert.True(path.Count >= 6);
            Assert.InRange(ReplayEdits(b) - ReplayEdits(a), 1, 80);                                          // a few edits, not a rewrite of the tree
        }
        finally { Directory.Delete(baseDir, true); Directory.Delete(extra, true); }
    }

    static long ReplayEdits(Engine e) => e.Graph("healthTree").Head;

    [Fact]
    public void The_lineage_of_a_tree_element_leads_down_to_the_master_element_and_the_evidence_to_the_event()
    {
        var h = new SpaceHost(Repo.DefinitionFilesFor(Wide), new HostOptions { Cluster = Cluster, LateHorizonWindows = 1_000_000 });
        using (h)
        {
            new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 200, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = Cluster }).Run(Wide);
            h.Finish();
            string IdOf(string graph, string key) => h.Engine.Graph(graph).State.Entities.Single(x => Ids.Name(x.Type) == "Element" && P(x, "nodeKey") == key).Id;
            var top = h.Lineage("healthTree", IdOf("healthTree", "pod/red/pay/ledger-7a1-1"));
            var s1 = top["sources"]!.AsArray().First(s => s!["graph"]!.GetValue<string>() == "treeStructure")!;
            Assert.False(string.IsNullOrEmpty(s1["element"]!.GetValue<string>()));                                                                                        // the element of the structure it was copied from
            var reverse = h.LineageReverse("treeStructure", s1["element"]!.GetValue<string>());
            Assert.Contains(reverse["partOf"]!.AsArray(), p => p!["graph"]!.GetValue<string>() == "healthTree");                                                              // and back up
            var evidence = h.Engine.Graph("healthTree").State.Entities.First(x => Ids.Name(x.Type) == "Evidence" && P(x, "evidenceKind") == "warning").Id;
            var down = h.Lineage("healthTree", evidence);
            Assert.Contains(down["sources"]!.AsArray(), s => s!["graph"]!.GetValue<string>() == "master" && s["role"]!.GetValue<string>() == "w");                                    // the warning event
            var back = h.LineageReverse("master", down["sources"]!.AsArray().First(s => s!["role"]!.GetValue<string>() == "w")!["element"]!.GetValue<string>());
            Assert.Contains(back["partOf"]!.AsArray(), p => p!["graph"]!.GetValue<string>() == "healthTree" && p["element"]!.GetValue<string>() == evidence);
        }
    }

    [Fact]
    public void The_tree_comes_back_from_a_store_with_the_same_state_digest_and_states()
    {
        var store = Path.Combine(Path.GetTempPath(), "sattle-tree-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            string digest; int unhealthy;
            using (var g = new SqliteGraphStore(store))
            using (var h = SpaceHost.OpenDurable(g, Repo.DefinitionFilesFor(Wide), new HostOptions { Cluster = Cluster, LateHorizonWindows = 1_000_000, SealEvery = 40 }))
            {
                new Replay(h, h.Space, h.Adapters, new ReplayOptions { ChunkRows = 100, AdvanceEveryNs = 60_000_000_000, LatenessNs = 60_000_000_000, Cluster = Cluster }).Run(Wide);
                h.Finish(); digest = h.StateDigest(); unhealthy = h.Engine.Graph("healthTree").State.Entities.Count(x => Ids.Name(x.Type) == "Element" && P(x, "rolledState") == "Unhealthy");
            }
            using var g2 = new SqliteGraphStore(store);
            using var again = SpaceHost.OpenDurable(g2, null, new HostOptions { Cluster = Cluster, VerifyJournal = true });
            Assert.True(again.Restored);
            Assert.Equal(digest, again.StateDigest());
            Assert.Equal(unhealthy, again.Engine.Graph("healthTree").State.Entities.Count(x => Ids.Name(x.Type) == "Element" && P(x, "rolledState") == "Unhealthy"));
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(store) + "*")) File.Delete(f); }
    }

    [Theory]
    [InlineData("shop", "ns/synth/shop")]
    [InlineData("db", "wl/synth/shop/db")]
    public void The_workload_model_and_the_tree_agree_on_the_state_of_what_both_cover(string component, string element)
    {
        var e = Run(Path.Combine(Fixtures, "estate"));
        var model = e.Graph("health").State.Entities.Where(x => Ids.Name(x.Type) == "Component").First(x => x.Props.Text("d:acme.health/prop:name") == component).Props.Text("d:acme.health/prop:state");
        Assert.Equal(model, State(e, element));
    }
}

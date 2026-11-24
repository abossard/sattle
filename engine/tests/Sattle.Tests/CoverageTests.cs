using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Language;
using Sattle.Runtime;

namespace Sattle.Tests;

public class CoverageTests
{
    const string File = "sattle.coverage.rules.gkql.json";
    static string Standard => Path.Combine(Repo.Root, "engine", "standard", "coverage");
    static readonly HostOptions Options = new() { SpaceName = "coverageReview", Projections = Repo.Projections };

    static JsonObject Rule(string id, string item, string target, string action = "bind", string role = "receiver", bool many = false) =>
        JsonSerializer.SerializeToNode(new { ruleId = id, revision = "owner-v1", source = "requests", item, role, target, action, many,
            owner = "team", reason = action == "exclude" ? "synthetic check" : "" })!.AsObject();

    static string Change(string text, JsonArray rules)
    {
        var p = JsonNode.Parse(text)!;
        p["verbs"]!.AsArray().Single(v => (string?)v!["id"] == "rules")!["values"]!["rows"] = rules.DeepClone();
        return p.ToJsonString();
    }

    static SpaceHost Open(JsonArray? rules = null, Dictionary<string, JsonNode>? scope = null, Action<List<SourceFile>>? configure = null)
    {
        var files = Catalog.ReadDirectory(Standard);
        var i = files.FindIndex(f => Path.GetFileName(f.Path) == File);
        files[i] = files[i] with { Text = Change(files[i].Text, rules ?? []) };
        if (scope is not null)
        {
            var c = files.FindIndex(f => Path.GetFileName(f.Path) == "sattle.coverage.module.gkql.json");
            var p = JsonNode.Parse(files[c].Text)!;
            var graph = p["space"]!["graphs"]!.AsArray().Single(g => (string?)g!["name"] == "coverage")!;
            foreach (var (name, value) in scope)
                graph["inputs"]![name] = new JsonObject { ["lit"] = value.DeepClone() };
            files[c] = files[c] with { Text = p.ToJsonString() };
        }
        configure?.Invoke(files);
        var host = new SpaceHost(files, Options);
        host.StartLive(new ReplayOptions { LatenessNs = 0 });
        return host;
    }

    static void Send(SpaceHost h, string row, string key, object values, int n)
    {
        var body = JsonSerializer.Serialize(new { v = 1, source = "records:" + row, seq = n, key,
            capturedAt = $"2026-01-01T00:00:{n:D2}Z", payload = new { key, values } });
        h.LiveEnvelope("capture", body, JsonSerializer.Serialize(new { seq = n }));
    }

    static void Observe(SpaceHost h, string item, int n, string source = "requests", string kind = "operation", string role = "receiver", long at = 10, string observedTarget = "web") =>
        Send(h, "RoleObservation", $"{source}/{item}/{role}", new { source, item, role, kind, at, observedTarget, sourceRef = $"{source}/{item}" }, n);

    static void Report(SpaceHost h, string source, string availability, int n) =>
        Send(h, "SourceScope", source, new { source, role = "receiver", reportId = "manifest", fromTime = 0, untilTime = long.MaxValue, availability, limits = "capture limits", sourceRef = source + "/manifest" }, n);

    static List<JsonNode> Findings(SpaceHost h, string direction) =>
        h.Entities("coverage", "Finding").Where(f => f.Prop("direction") == direction).ToList();

    [Fact]
    public void A_review_follows_the_rules_port_and_its_producing_file_through_aliases()
    {
        using var h = Open(configure: files =>
        {
            var r = files.FindIndex(f => Path.GetFileName(f.Path) == File);
            var rules = JsonNode.Parse(files[r].Text)!;
            rules["graph"] = "ownerPolicy";
            files[r] = new SourceFile(Path.Combine(Standard, "sattle.coverage.ownerPolicy.gkql.json"), rules.ToJsonString());
            var m = files.FindIndex(f => Path.GetFileName(f.Path) == "sattle.coverage.module.gkql.json");
            var module = JsonNode.Parse(files[m].Text)!;
            var graphs = module["space"]!["graphs"]!.AsArray();
            var policy = graphs.Single(g => (string?)g!["name"] == "rules")!;
            policy["name"] = "reviewedDeclarations";
            policy["projection"] = "ownerPolicy";
            graphs.Single(g => (string?)g!["name"] == "coverage")!["inputs"]!["rules"] = "reviewedDeclarations";
            files[m] = files[m] with { Text = module.ToJsonString() };
        });
        var graphs = h.SpaceInfo()["graphs"]!.AsArray();
        var coverage = graphs.Single(g => (string?)g!["id"] == "coverage")!;
        var bound = (string?)coverage["inputPorts"]!["rules"];
        Assert.Equal("reviewedDeclarations", bound);
        Assert.Equal("sattle.coverage.ownerPolicy.gkql.json", (string?)graphs.Single(g => (string?)g!["id"] == bound)!["projectionFile"]);
        Assert.Empty(graphs.Single(g => (string?)g!["id"] == "master")!["inputPorts"]!.AsObject());
    }

    [Fact]
    public void Empty_ontology_keeps_novel_entity_relation_and_operation_through_the_adapter()
    {
        using var h = Open();
        foreach (var (n, kind) in new[] { "entity", "relationship", "operation" }.Index())
            Observe(h, $"new-{n}", n + 1, kind: kind, role: "catalogue");
        AssertNovelPopulation(h, "requests");
    }

    [Fact]
    public void A_press_log_with_its_own_contract_uses_the_same_coverage_and_rules()
    {
        using var h = Open(configure: files =>
        {
            void Edit(string file, Action<JsonNode> change)
            {
                var i = files.FindIndex(f => Path.GetFileName(f.Path) == "sattle.coverage." + file + ".gkql.json");
                var doc = JsonNode.Parse(files[i].Text)!;
                change(doc);
                files[i] = files[i] with { Text = doc.ToJsonString() };
            }
            Edit("module", m =>
            {
                m["rows"]![0] = JsonNode.Parse("""
                    {"name":"PressLog","fields":[{"name":"ticket","type":"Text"},{"name":"entry","type":"Text"},{"name":"station","type":"Text"},{"name":"clock","type":"Integer"}]}
                    """);
                m["space"]!["sources"]![0]!["rows"] = "PressLog";
            });
            Edit("ingest", p =>
            {
                p["inputs"]![0]!["rows"] = "sattle.coverage.PressLog";
                p["verbs"]!.AsArray().Add(JsonNode.Parse("""
                    {"id":"readPressLog","verb":"project","columns":{
                      "$recordKey":{"ref":"ticket"},"source":{"lit":"printshop"},"item":{"ref":"ticket"},"role":{"lit":"catalogue"},
                      "kind":{"fn":"iff","args":[{"fn":"==","args":[{"ref":"entry"},{"lit":"installed"}]},{"lit":"entity"},
                             {"fn":"iff","args":[{"fn":"==","args":[{"ref":"entry"},{"lit":"supplies"}]},{"lit":"relationship"},{"lit":"operation"}]}]},
                      "at":{"ref":"clock"},"observedTarget":{"ref":"station"},"sourceRef":{"fn":"concat","args":[{"lit":"printshop/"},{"ref":"ticket"}]}}}
                    """));
                p["wires"]![0]!["from"] = "readPressLog";
                p["wires"]!.AsArray().Add(JsonNode.Parse("""{"from":"observations","to":"readPressLog"}"""));
            });
        });
        foreach (var (n, entry) in new[] { "installed", "supplies", "unseen-press-operation" }.Index())
            Send(h, "PressLog", $"ticket-{n}", new { ticket = $"ticket-{n}", entry, station = "press-7", clock = 100 + n }, n + 1);
        AssertNovelPopulation(h, "printshop");
    }

    static void AssertNovelPopulation(SpaceHost h, string source)
    {
        h.LiveFlush();
        Assert.Equal(3, h.Entities("master", "Observation").Count);
        var findings = Findings(h, "observation");
        Assert.Equal(new[] { "entity", "operation", "relationship" }, findings.Select(f => f.Prop("kind")).Order());
        Assert.All(findings, f => { Assert.Equal("unmapped", f.Prop("status")); Assert.Equal("catalogue", f.Prop("role"));
            Assert.Equal(source + "/" + f.Prop("item"), f.Prop("sourceRef")); Assert.False(string.IsNullOrEmpty(f.Prop("policyRevision"))); });
    }

    [Fact]
    public void Five_items_count_before_rules_and_distinct_targets_determine_cardinality()
    {
        JsonArray rules = [Rule("a", "one", "same"), Rule("b", "one", "same"), Rule("c", "two", "x", many: true),
            Rule("d", "two", "y", many: true), Rule("e", "four", "x"), Rule("f", "four", "y"), Rule("g", "five", "", "exclude")];
        using var h = Open(rules);
        foreach (var (n, item) in new[] { "one", "two", "three", "four", "five" }.Index()) Observe(h, item, n + 1);
        h.LiveFlush();
        Assert.Equal(new[] { "mapped", "mapped", "unmapped", "ambiguous", "excluded" },
            new[] { "one", "two", "three", "four", "five" }.Select(i => Findings(h, "observation").Single(f => f.Prop("item") == i).Prop("status")));
        var scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("5", scope.Prop("eligible")); Assert.Equal("3", scope.Prop("supportedBindings"));
        var exclusion = Findings(h, "declaration").Single(f => f.Prop("ruleId") == "g");
        Assert.Equal("synthetic check", exclusion.Prop("reason")); Assert.Equal("team", exclusion.Prop("owner")); Assert.Equal("owner-v1", exclusion.Prop("revision"));
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        rules.Add(Rule("h", "three", "", "exclude"));
        Assert.True((bool)h.SetDraftText(draft, File, Change(h.Definition(File), rules), 0)["ok"]!);
        h.CommitDraft(draft, h.CatalogRevision);
        scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("5", scope.Prop("eligible")); Assert.Equal("3", scope.Prop("supportedBindings")); Assert.Equal("2", scope.Prop("excluded"));
    }

    [Fact]
    public void Same_request_same_role_and_target_are_required_for_a_declaration()
    {
        JsonArray rules = [Rule("r", "submit-42", "worker", "declare"), Rule("d", "submit-42", "worker", "declare", "downstream"),
            Rule("other", "other-id", "web", "declare"), Rule("dependency", "submit-42", "db", "declare", "requires")];
        using var h = Open(rules);
        Observe(h, "submit-42", 1); h.LiveFlush();
        var conflict = Findings(h, "declaration").Single(f => f.Prop("ruleId") == "r");
        Assert.Equal("conflict", conflict.Prop("status")); Assert.Equal("web", conflict.Prop("observedTarget")); Assert.Equal("requests/submit-42", conflict.Prop("sourceRef"));
        Assert.All(Findings(h, "declaration").Where(f => f.Prop("ruleId") != "r"), f => Assert.Equal("unverified", f.Prop("status")));
        Assert.Equal("conflict", Assert.Single(h.Entities("conflicts", "Finding")).Prop("status"));
        var rev = long.Parse(h.Descriptor("coverage")["version"]!["revision"].Id());
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        rules[0]!["target"] = "web"; rules[0]!["revision"] = "owner-v2";
        Assert.True((bool)h.SetDraftText(draft, File, Change(h.Definition(File), rules), 0)["ok"]!);
        var preview = h.Preview(draft, JsonNode.Parse("""{"graphs":["coverage"],"include":["snapshots","diff"]}""")!)["graphs"]![0]!;
        Assert.Contains(preview["snapshot"]!["extent"]!["entities"]!.AsArray(), f => f!.Prop("ruleId") == "r" && f!.Prop("status") == "mapped");
        Assert.Equal("conflict", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "r").Prop("status"));
        h.CommitDraft(draft, h.CatalogRevision);
        Assert.Equal("mapped", Findings(h, "observation").Single().Prop("status"));
        Assert.Empty(h.Entities("conflicts", "Finding"));
        Assert.Equal("owner-v2", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "r").Prop("revision"));
        Assert.Contains(h.Snapshot("coverage", rev)["extent"]!["entities"]!.AsArray(), f => f!.Prop("ruleId") == "r" && f!.Prop("status") == "conflict");
    }

    [Fact]
    public void Dependency_class_changes_cannot_supply_receiver_or_linked_call_evidence()
    {
        JsonArray rules = [Rule("ingress", "submit-42", "web", "declare"), Rule("requires", "submit-42", "web", "declare", "requires"),
            Rule("call", "submit-42", "worker", "declare", "downstream")];
        using var h = Open(rules);
        Observe(h, "submit-42", 1);
        Observe(h, "another-request", 2, role: "downstream", observedTarget: "worker");
        h.LiveFlush();
        var receiver = h.Entities("coverage", "Scope").Single(s => s.Prop("role") == "receiver").DeepClone();
        rules[1]!["target"] = "worker";
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        Assert.True((bool)h.SetDraftText(draft, File, Change(h.Definition(File), rules), 0)["ok"]!);
        h.CommitDraft(draft, h.CatalogRevision);
        Assert.True(JsonNode.DeepEquals(receiver, h.Entities("coverage", "Scope").Single(s => s.Prop("role") == "receiver")));
        Assert.Equal("unverified", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "call").Prop("status"));
        Observe(h, "submit-42", 3, kind: "relationship", role: "downstream", observedTarget: "worker");
        h.LiveFlush();
        Assert.Equal("mapped", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "call").Prop("status"));
        Assert.Equal("mapped", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "ingress").Prop("status"));
        Assert.Equal("unverified", Findings(h, "declaration").Single(f => f.Prop("ruleId") == "requires").Prop("status"));
    }

    [Fact]
    public void Rule_ids_are_unique_and_an_incomplete_exclusion_cannot_remove_an_observation()
    {
        var invalid = Rule("invalid", "job", "", "exclude");
        invalid["owner"] = "";
        using var h = Open([invalid]);
        Observe(h, "job", 1);
        h.LiveFlush();
        Assert.Equal("unmapped", Assert.Single(Findings(h, "observation")).Prop("status"));
        Assert.Equal("invalid", Assert.Single(Findings(h, "declaration")).Prop("status"));
        var draft = h.CreateDraft(null)["draft"]!.GetValue<string>();
        var result = h.SetDraftText(draft, File, Change(h.Definition(File), [Rule("duplicate", "x", "y"), Rule("duplicate", "z", "a")]), 0);
        Assert.False((bool)result["ok"]!);
        Assert.Contains("distinct nonempty ruleId", result["diagnostics"]!.ToJsonString());
    }

    [Fact]
    public void Two_receipts_at_different_times_leave_one_unmapped_item_in_a_complete_scope()
    {
        using var h = Open(scope: new() { ["scopeSource"] = "requests", ["scopeRole"] = "receiver", ["fromTime"] = 0, ["untilTime"] = 20 });
        foreach (var at in new[] { 10, 11 })
            Send(h, "RoleObservation", $"receipt-{at}", new { source = "requests", item = "submit-42", role = "receiver",
                kind = "operation", observedTarget = "web", at }, at);
        Send(h, "SourceScope", "manifest", new { source = "requests", role = "receiver", reportId = "manifest",
            availability = "complete", fromTime = 0, untilTime = 20 }, 12);
        h.LiveFlush();

        Assert.Equal(2, h.Entities("master", "Observation").Count);
        Assert.Equal("unmapped", Assert.Single(Findings(h, "observation")).Prop("status"));
        var scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("complete", scope.Prop("availability"));
        Assert.Equal("1", scope.Prop("eligible"));
        Assert.Equal("1", scope.Prop("unmapped"));
        Assert.Equal("0", scope.Prop("supportedBindings"));
    }

    [Fact]
    public void Multiple_participant_receipts_count_once_per_item_and_support_each_distinct_target()
    {
        using var h = Open([Rule("a", "job", "worker-a", "declare", role: "participant", many: true),
            Rule("b", "job", "worker-b", "declare", role: "participant", many: true)]);
        void Receipt(string key, string target, int seq) =>
            Send(h, "RoleObservation", key, new { source = "requests", item = "job", role = "participant", kind = "operation",
                observedTarget = target, sourceRef = "same-batch", at = 10 }, seq);
        Receipt("span-a", "worker-a", 1); Receipt("span-b", "worker-b", 2); Receipt("retry-a", "worker-a", 3);
        h.LiveFlush();
        Assert.Equal(3, h.Entities("master", "Observation").Count);
        var scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("1", scope.Prop("eligible")); Assert.Equal("2", scope.Prop("supportedBindings")); Assert.Equal("1", scope.Prop("mapped"));
        var item = Assert.Single(Findings(h, "observation"));
        Assert.Equal("mapped", item.Prop("status")); Assert.Null(item.Prop("observedTarget"));
        Assert.All(Findings(h, "declaration"), r => Assert.Equal("mapped", r.Prop("status")));
        var receipts = h.Entities("coverage", "Observation");
        Assert.Equal(new[] { "retry-a", "span-a", "span-b" }, receipts.Select(r => r.Prop("receiptKey")).Order());
        Assert.All(receipts, r => Assert.Equal("same-batch", r.Prop("sourceRef")));
        var evidence = h.Relationships("coverage", "BasedOn");
        Assert.Equal(3, evidence.Count(e => e["source"].Id() == item["id"].Id()));
        var firstWorker = Findings(h, "declaration").Single(r => r.Prop("ruleId") == "a");
        var supporting = evidence.Where(e => e["source"].Id() == firstWorker["id"].Id() && e.Prop("assessment") == "mapped");
        Assert.Equal(new[] { "retry-a", "span-a" }, supporting.Select(e => receipts.Single(r => r["id"].Id() == e["target"].Id()).Prop("receiptKey")).Order());
        foreach (var (index, withdrawn) in new[] { "span-a", "retry-a" }.Index())
        {
            var seq = 4 + index;
            var body = JsonSerializer.Serialize(new { v = 1, source = "records:RoleObservation", seq, key = withdrawn,
                capturedAt = $"2026-01-01T00:00:{seq:D2}Z", payload = new { key = withdrawn, deleted = true } });
            h.LiveEnvelope("capture", body, JsonSerializer.Serialize(new { seq }));
        }
        h.LiveFlush();
        Assert.Equal("worker-b", Assert.Single(Findings(h, "observation")).Prop("observedTarget"));
        Assert.Equal("unverified", Findings(h, "declaration").Single(r => r.Prop("ruleId") == "a").Prop("status"));
        Assert.Equal("mapped", Findings(h, "declaration").Single(r => r.Prop("ruleId") == "b").Prop("status"));
        Assert.Equal("1", Assert.Single(h.Entities("coverage", "Scope")).Prop("supportedBindings"));
    }

    [Fact]
    public void A_valid_exclusion_of_an_ambiguous_item_changes_its_bucket_and_preserves_the_population()
    {
        using var h = Open([Rule("a", "job", "worker-a"), Rule("b", "job", "worker-b"), Rule("exception", "job", "", "exclude")]);
        Observe(h, "job", 1); h.LiveFlush();
        var scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("1", scope.Prop("eligible")); Assert.Equal("0", scope.Prop("supportedBindings"));
        Assert.Equal("1", scope.Prop("excluded")); Assert.Equal("0", scope.Prop("ambiguous"));
        Assert.Equal("excluded", Assert.Single(Findings(h, "observation")).Prop("status"));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(20)]
    public void A_corrected_outage_with_no_positive_interval_cannot_poison_complete_coverage(int correctedEnd)
    {
        using var h = Open(scope: new() { ["scopeSource"] = "requests", ["scopeRole"] = "receiver", ["fromTime"] = 0, ["untilTime"] = 100 });
        void Manifest(string key, int from, int until, string availability, int seq) =>
            Send(h, "SourceScope", key, new { source = "requests", role = "receiver", reportId = key, availability, fromTime = from, untilTime = until }, seq);
        Manifest("all", 0, 100, "complete", 1); Manifest("outage", 25, 30, "unavailable", 2); h.LiveFlush();
        Assert.Equal("unknown", Assert.Single(h.Entities("coverage", "Scope")).Prop("availability"));
        Manifest("outage", 25, correctedEnd, "unavailable", 3); h.LiveFlush();
        Assert.Equal("complete", Assert.Single(h.Entities("coverage", "Scope")).Prop("availability"));
        Assert.Single(h.Entities("coverage", "SourceReport"));
    }

    [Fact]
    public void Source_manifest_reports_limits_independently_of_the_denominator()
    {
        using var h = Open();
        foreach (var (n, state) in new[] { "complete", "unavailable", "sampled", "filtered" }.Index()) Report(h, state, state, n + 1);
        h.LiveFlush();
        foreach (var s in h.Entities("coverage", "Scope"))
        {
            Assert.Equal(s.Prop("source") == "unavailable" ? "unknown" : s.Prop("source"), s.Prop("availability"));
            Assert.Equal("0", s.Prop("eligible")); Assert.Equal("0", s.Prop("supportedBindings"));
        }
        Assert.Equal(4, h.Entities("coverage", "Scope").Count);
        Assert.All(h.Entities("coverage", "SourceReport"), r => Assert.Equal("capture limits", r.Prop("limits")));
    }

    [Fact]
    public void A_named_scope_without_a_manifest_or_observations_is_unknown()
    {
        using var h = Open(scope: new() { ["scopeSource"] = "silent", ["scopeRole"] = "operator" });
        Observe(h, "elsewhere", 1);
        h.LiveFlush();
        var scope = Assert.Single(h.Entities("coverage", "Scope"));
        Assert.Equal("silent", scope.Prop("source")); Assert.Equal("operator", scope.Prop("role"));
        Assert.Equal("unknown", scope.Prop("availability")); Assert.Equal("0", scope.Prop("eligible"));
        Assert.Equal("0", scope.Prop("supportedBindings")); Assert.Empty(Findings(h, "observation"));
    }

    [Fact]
    public void Source_role_and_half_open_event_time_window_are_independent_of_the_rules()
    {
        using var h = Open(scope: new() { ["scopeSource"] = "requests", ["scopeRole"] = "receiver", ["fromTime"] = 10, ["untilTime"] = 20 });
        Observe(h, "before", 1, at: 9); Observe(h, "first", 2, at: 10); Observe(h, "last", 3, at: 19); Observe(h, "after", 4, at: 20);
        Observe(h, "other-source", 5, source: "logs"); Observe(h, "other-role", 6, role: "caller"); h.LiveFlush();
        Assert.Equal(new[] { "first", "last" }, Findings(h, "observation").Select(f => f.Prop("item")).Order());
        Assert.Equal(6, h.Entities("master", "Observation").Count);
        Send(h, "SourceScope", "partial", new { source = "requests", role = "receiver", reportId = "short", availability = "complete", fromTime = 12, untilTime = 19 }, 7);
        h.LiveFlush();
        Assert.Equal("partial", Assert.Single(h.Entities("coverage", "Scope")).Prop("availability"));
    }
    [Theory]
    [InlineData(0, 10, 10, 20, "complete")]
    [InlineData(-4, 11, 9, 23, "complete")]
    [InlineData(0, 9, 10, 20, "partial")]
    [InlineData(1, 12, 10, 21, "partial")]
    [InlineData(0, 12, 10, 19, "partial")]
    public void Complete_manifests_cover_the_union_of_their_half_open_intervals(
        int firstStart, int firstEnd, int secondStart, int secondEnd, string availability)
    {
        using var h = Open(scope: new() { ["scopeSource"] = "requests", ["scopeRole"] = "receiver", ["fromTime"] = 0, ["untilTime"] = 20 });
        void Manifest(string key, int start, int end, string status, int seq, string source = "requests", string role = "receiver") =>
            Send(h, "SourceScope", key, new { source, role, reportId = key, availability = status, fromTime = start, untilTime = end }, seq);
        string Status() => Assert.Single(h.Entities("coverage", "Scope")).Prop("availability")!;

        Manifest("second", secondStart, secondEnd, "complete", 1);
        Manifest("first", firstStart, firstEnd, "complete", 2);
        Manifest("other-source", 0, 20, "complete", 3, source: "elsewhere");
        Manifest("other-role", 0, 20, "complete", 4, role: "operator");
        h.LiveFlush();
        Assert.Equal(availability, Status());

        Manifest("first", 1, 2, "complete", 5);
        h.LiveFlush();
        Assert.Equal("partial", Status());
        Manifest("bridge", -1, secondStart, "complete", 6);
        Manifest("tail", secondEnd, 25, "complete", 7);
        h.LiveFlush();
        Assert.Equal("complete", Status());

        foreach (var (offset, limit) in new[] { "sampled", "filtered", "unavailable" }.Index())
        {
            Manifest(limit, 5, 6, limit, 8 + offset);
            h.LiveFlush();
            Assert.Equal(limit == "unavailable" ? "unknown" : limit, Status());
        }
    }

}

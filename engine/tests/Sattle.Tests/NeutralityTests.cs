using System.Text.RegularExpressions;

namespace Sattle.Tests;

/// <summary>The engine projects name no domain: no health states, no authored health rows, no Azure, Kubernetes or other source system (#144), no observability word for events and windows (#166), no estate, workload or query language of a use case (#183). Domains come in through definitions and record envelopes only. The engine may name its own health: its liveness route is `"/health"`, matched by that exact form.</summary>
public partial class NeutralityTests
{
    [GeneratedRegex(@"Healthy|Degraded|Unhealthy|SignalRule|PolicyRow|CurationRow|RollupPolicy|(?<!""/)[Hh]ealth|""/Health|""/health(?!"")|[Aa]zure|[Kk]ubernetes|\bk8s\b|kubectl|\b[Pp]ods?\b|\b[Pp]ods?(?=[A-Z_0-9])|(?<=[a-z])Pods?(?![a-z])|[Hh]ubble|[Pp]rometheus|[Ee]vent ?[Hh]ubs?\b|eventhub|ResourceGraph|\bAKS\b|\baks\b|[Cc]luster|[Tt]elemetry|Deployment|deployment(?=[A-Z])|[Rr]eplica ?[Ss]et|[Rr]esource ?[Gg]roup|resource-group|/subscriptions/|[Ss]ubscription ?[Ii]ds?\b|[Kk]usto|(?<![Gg])(?:KQL|Kql|kql)|\b[Aa]cme|ACME|(?<![A-Za-z])estate|Estate|[Cc]osmos")]
    private static partial Regex DomainWord();

    /// <summary>The domain words of a source text: (line, symbol, match). The symbol is the identifier or word the match is in.</summary>
    public static IEnumerable<(int Line, string Symbol, string Match)> Scan(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match m in DomainWord().Matches(lines[i]))
            {
                int from = m.Index, to = m.Index + m.Length;
                while (from > 0 && (char.IsLetterOrDigit(lines[i][from - 1]) || lines[i][from - 1] is '_' or '.')) from--;
                while (to < lines[i].Length && (char.IsLetterOrDigit(lines[i][to]) || lines[i][to] == '_')) to++;
                yield return (i + 1, lines[i][from..to], m.Value);
            }
        }
    }

    /// <summary>Every engine project: each folder under engine/src with a project file.</summary>
    static List<string> Projects() => [.. Directory.EnumerateDirectories(Path.Combine(Repo.Root, "engine", "src"))
        .Where(d => Directory.EnumerateFiles(d, "*.csproj").Any()).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];
    public static TheoryData<string> EngineProjects => [.. Projects()];

    [Fact]
    public void The_guard_scans_the_command_line() => Assert.Contains("Sattle.Cli", Projects());

    [Theory]
    [MemberData(nameof(EngineProjects))]
    public void An_engine_project_names_no_domain(string assembly)
    {
        var dir = Path.Combine(Repo.Root, "engine", "src", assembly);
        var found = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Scan(File.ReadAllText(f)).Select(x => $"{Path.GetRelativePath(dir, f)}:{x.Line} {x.Symbol} ({x.Match})"))
            .ToList();
        Assert.True(found.Count == 0, $"{assembly} names a domain:\n{string.Join("\n", found.Take(40))}");
    }

    [Theory]
    [InlineData("var s = \"Unhealthy\";", "Unhealthy")]
    [InlineData("sealed class SignalRuleRow { }", "SignalRule")]
    [InlineData("// read the pods of the node", "pods")]
    [InlineData("new KubernetesAdapter(node)", "Kubernetes")]
    [InlineData("source = \"eventhub:\" + name;", "eventhub")]
    [InlineData("const string Id = \"azure.metrics\";", "azure")]
    [InlineData("rows MetricRow from prometheus", "prometheus")]
    [InlineData("var d = row.cluster;", "cluster")]
    [InlineData("interface ITelemetryStore { }", "Telemetry")]
    [InlineData("// the telemetry of the step", "telemetry")]
    [InlineData("Ids.Name(x.Type) is \"Deployment\"", "Deployment")]
    [InlineData("var deploymentReady = g;", "deployment")]
    [InlineData("// the ReplicaSet of the set", "ReplicaSet")]
    [InlineData("var t = \"AzureResource\";", "Azure")]
    [InlineData("var rg = row.resourceGroup;", "resourceGroup")]
    [InlineData("// the resource group of the run", "resource group")]
    [InlineData("var id = \"/subscriptions/0000/x\";", "/subscriptions/")]
    [InlineData("string SubscriptionId = \"\";", "SubscriptionId")]
    [InlineData("var q = KustoQuery(text);", "Kusto")]
    [InlineData("app.MapGet(\"/kql/check\", Check);", "kql")]
    [InlineData("// a KQL text", "KQL")]
    [InlineData("var p = \"d:acme.svc/prop:name\";", "acme")]
    [InlineData("sattle bench gen-estate --nodes 10", "estate")]
    [InlineData("static int GenEstate(Options o)", "Estate")]
    [InlineData("var podCode = 1;", "pod")]
    [InlineData("var c = PodIp;", "Pod")]
    [InlineData("var r = PodsReady;", "Pods")]
    [InlineData("var m = podsByNode;", "pods")]
    [InlineData("var p = nodePod;", "Pod")]
    [InlineData("var k = kubePod;", "Pod")]
    [InlineData("var n = nodePods;", "Pods")]
    [InlineData("app.MapGet(\"/Health\", Live);", "\"/Health")]
    [InlineData("app.MapGet(\"/HealthTree\", Live);", "\"/Health")]
    [InlineData("var t = \"healthTree\";", "health")]
    [InlineData("var m = HealthModel.Of(g);", "Health")]
    [InlineData("app.MapGet(\"/healthz\", Live);", "\"/health")]
    [InlineData("// served by GET /health", "health")]
    [InlineData("<PackageReference Include=\"Azure.Identity\" />", "Azure")]
    [InlineData("var c = new CosmosClient(e);", "Cosmos")]
    public void A_planted_domain_literal_is_found(string line, string match) =>
        Assert.Equal(match, Assert.Single(Scan(line)).Match);

    [Theory]
    [InlineData("sealed class FoldOp : PhysOp")]
    [InlineData("\"fold\" => Fold(v, ins),")]
    [InlineData("sealed class RollupOp : AggregatingOp")]
    [InlineData("new Diagnostic(\"Aggregate\", \"error\", focus, message) { Severity = \"error\" }")]
    [InlineData("var spods = epods + 1; // podium, Podcast")]
    [InlineData("var x = Podium + nodePodcast;")]
    [InlineData("Tenant = \"lab\", // the tenant of the run")]
    [InlineData("// a subscription of a publish-subscribe topic, Subscribe(handler)")]
    [InlineData("// the container of a node in the tree, Contains")]
    [InlineData("// an OS signal: SIGTERM ends the input, PosixSignal.SIGINT")]
    [InlineData("app.MapGet(\"/health\", Liveness);")]
    [InlineData("var gkql = new Sattle.Gkql.GkqlSource(); // a .gkql.json file")]
    [InlineData("// restated, reinstate, the deployment of the engine")]
    public void A_generic_symbol_passes(string line) => Assert.Empty(Scan(line));
}

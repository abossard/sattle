using System.Text.RegularExpressions;

namespace Sattle.Tests;

/// <summary>The core assemblies name no domain: no health states, no authored health rows, no Azure, Kubernetes or other source system (#144). Domains come in through definitions and record envelopes only.</summary>
public partial class NeutralityTests
{
    [GeneratedRegex(@"Healthy|Degraded|Unhealthy|SignalRule|PolicyRow|CurationRow|RollupPolicy|[Hh]ealth|[Aa]zure|[Kk]ubernetes|\bk8s\b|kubectl|\b[Pp]ods?\b|[Hh]ubble|[Pp]rometheus|[Ee]vent ?[Hh]ubs?\b|eventhub|ResourceGraph|\bAKS\b|\baks\b|[Cc]luster")]
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

    public static TheoryData<string> CoreAssemblies => ["Sattle.Core", "Sattle.Language", "Sattle.Runtime", "Sattle.Gkql"];

    [Theory]
    [MemberData(nameof(CoreAssemblies))]
    public void A_core_assembly_names_no_domain(string assembly)
    {
        var dir = Path.Combine(Repo.Root, "engine", "src", assembly);
        var found = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
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
    public void A_planted_domain_literal_is_found(string line, string match) =>
        Assert.Equal(match, Assert.Single(Scan(line)).Match);

    [Theory]
    [InlineData("sealed class ThresholdOp : PhysOp")]
    [InlineData("case \"hysteresis\" => Threshold(v, One()),")]
    [InlineData("sealed class RollupOp : AggregatingOp")]
    [InlineData("new Diagnostic(\"Aggregate\", \"error\", focus, message) { Severity = \"error\" }")]
    [InlineData("var spods = epods + 1; // podium, Podcast")]
    public void A_generic_symbol_passes(string line) => Assert.Empty(Scan(line));
}

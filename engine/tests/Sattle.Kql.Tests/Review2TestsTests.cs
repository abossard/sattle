using Sattle.Cli;

namespace Sattle.Kql.Tests;

public class Review2TestsTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "engine", "Sattle.slnx"))) d = d.Parent;
        return d!.FullName;
    }

    // HEAD: with --projections kql made a no-op, the replay uses today's text and ends in the same digest, so the existing test cannot tell; a changed KQL file must change the digest.
    [Fact]
    public void Replaying_with_the_projections_in_KQL_reads_the_KQL_files()
    {
        var root = Root();
        var estate = Path.Combine(root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");
        var defs = Directory.CreateTempSubdirectory("sattle-kql-defs").FullName;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "engine", "definitions"))) File.Copy(f, Path.Combine(defs, Path.GetFileName(f)));
        var report = Path.Combine(defs, "acme.report.deploymentReport.kql");
        var text = File.ReadAllText(report);
        Assert.Contains("\"Deployment health\"", text);
        File.WriteAllText(report, text.Replace("\"Deployment health\"", "\"Deployment health, from KQL\""));
        string Digest(params string[] more)
        {
            var output = new StringWriter(); var saved = Console.Out; Console.SetOut(output);
            try { Assert.Equal(0, Program.Main(["replay", estate, "--cluster", "synth", "--defs", defs, .. more])); } finally { Console.SetOut(saved); }
            return output.ToString().Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("state digest ", StringComparison.Ordinal));
        }
        Assert.NotEqual(Digest(), Digest("--projections", "kql"));
    }
}

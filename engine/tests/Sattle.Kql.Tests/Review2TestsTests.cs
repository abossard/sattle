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

    // a changed KQL file must change the digest of a replay
    [Fact]
    public void A_replay_reads_the_KQL_files()
    {
        var root = Root();
        var estate = Path.Combine(root, "engine", "tests", "Sattle.Tests", "fixtures", "estate");
        var defs = Directory.CreateTempSubdirectory("sattle-kql-defs").FullName;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "engine", "definitions"))) File.Copy(f, Path.Combine(defs, Path.GetFileName(f)));
        var report = Path.Combine(defs, "acme.report.deploymentReport.kql");
        var text = File.ReadAllText(report);
        Assert.Contains("\"Deployment health\"", text);
        string Digest()
        {
            var output = new StringWriter();
            Assert.Equal(0, Program.Run(["replay", estate, "--cluster", "synth", "--defs", defs], output));
            return output.ToString().Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith("state digest ", StringComparison.Ordinal));
        }
        var before = Digest();
        File.WriteAllText(report, text.Replace("\"Deployment health\"", "\"Deployment health, from KQL\""));
        Assert.NotEqual(before, Digest());
    }
}

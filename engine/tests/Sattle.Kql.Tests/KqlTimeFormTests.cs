using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>D3 (#101): the time forms of the KQL rules, written into the pod warnings of the master: shift (`t + 90s`), hop windows (`hop(t, 3m, 60s)`), and what the front end and the checker refuse.</summary>
public class KqlTimeFormTests
{
    static readonly Lazy<DirectoryInfo> Definitions = new(() =>
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return new DirectoryInfo(Path.Combine(d!.FullName, "engine", "definitions"));
    });

    const string Written = "let podWarnings = events | where ['kind'] == \"Pod\" and ['type'] == \"Warning\" | summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = bin(['time'], 60s);";

    /// <summary>The errors of the catalog with the pod warnings written as `podWarnings`.</summary>
    static List<string> Errors(string podWarnings)
    {
        var files = Language.Catalog.ReadDirectory(Definitions.Value.FullName).ToList();
        var i = files.FindIndex(f => f.Path.EndsWith("acme.ops.ingest.kql", StringComparison.Ordinal));
        Assert.Contains(Written, files[i].Text);
        files[i] = files[i] with { Text = files[i].Text.Replace(Written, podWarnings, StringComparison.Ordinal) };
        var res = new Catalog(new KqlSource()).Load(files, updatePins: true);
        return [.. res.Diagnostics.Where(x => x.IsError).Select(x => x.ToString())];
    }

    [Theory]
    [InlineData("summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = hop(['time'], 3m, 60s)", null)]
    [InlineData("extend later = ['time'] + 90s | summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = bin(later, 60s)", null)]
    [InlineData("extend earlier = ['time'] - 1h | summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = bin(earlier, 60s)", null)]
    [InlineData("summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = hop(['time'], 3m, 70s)", "the hop of a window must be positive and divide its width")]
    [InlineData("summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = hop(['time'], 60s, 3m)", "the hop of a window must be positive and divide its width")]
    [InlineData("summarize warnings = sum(['count']) by cluster, namespace, name, at = hop(['time'], 3m, 60s) | extend windowStart = at", "windowStart = hop(time, 5m, 1m)")]
    [InlineData("summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = hop(['time'], 60s, 60s)", null)]
    [InlineData("extend twice = ['time'] + ['time'] | summarize warnings = sum(['count']) by cluster, namespace, name, windowStart = bin(twice, 60s)", "Add needs two numbers")]
    public void A_time_form_is_accepted_or_refused_with_the_reason(string form, string? refusal)
    {
        var errors = Errors($"let podWarnings = events | where ['kind'] == \"Pod\" and ['type'] == \"Warning\" | {form};");
        if (refusal is null) Assert.True(errors.Count == 0, string.Join("\n", errors));
        else Assert.True(errors.Any(e => e.Contains(refusal, StringComparison.Ordinal)), string.Join("\n", errors.Take(3)));
    }
}

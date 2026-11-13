using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2, time: where the front end reads a constant (helpers as in KqlProjectionTests).</summary>
public class Review2TimeKqlTests
{
    static readonly Lazy<Graph> Catalog = new(() =>
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        var cat = new Catalog();
        var res = cat.Load(Language.Catalog.ReadDirectory(Path.Combine(d!.FullName, "engine", "definitions")).ToList(), updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, x => x.IsError);
        return cat.Graph;
    });

    const string Head = "graph serviceDeps : acme.svc;\ninput master : acme.ops;\n"
        + "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";

    static KqlResult Lower(string rest) => KqlFrontEnd.Lower(Head + rest, Catalog.Value, "acme.svc");

    /// <summary>HEAD: a computed constant (-1, now()) has IsConstant true and ConstantValue null, so the literal written is null and no diagnostic is given; a query parameter is replaced by its default.</summary>
    [Theory]
    [InlineData("extend nodes ServiceNode on service = services | summarize n = count() by service | invoke above(-1) | project service, pods = n", "-1")]
    [InlineData("extend nodes ServiceNode on service = services | summarize n = count() by service | invoke above(now()) | project service, pods = n", "now")]
    [InlineData("extend nodes ServiceNode on service = services | summarize n = count() by service | invoke above(ago(1h)) | project service, pods = n", "ago")]
    [InlineData("extend nodes ServiceNode on service = services | summarize n = count() by service | invoke above(minPods) | project service, pods = n", "minPods")]
    public void A_constant_given_to_a_function_or_as_a_fill_is_the_value_written_or_a_diagnostic(string step, string written)
    {
        var low = Lower((written == "minPods" ? "declare query_parameters(minPods:long = 3);\n" : "")
            + "let above = (T:(service:string, n:long), limit:long) { T | where n > limit };\nnodes ServiceNode on service = services;\n" + step + ";\n");
        // a null literal, or a literal 3 for the parameter, in what was written without a diagnostic of the front end
        var literals = low.Facts.Elements().OfType<Entity>().Where(e => Ids.Name(e.Type) is "Literal").ToList();
        var nulls = literals.Count(e => e.Props.Items.All(kv => kv.Value is null or NullValue));
        Assert.True(!low.Ok || (nulls == 0 && !literals.Any(e => e.Props.Items.Any(kv => kv.Value is IntegerValue iv && iv.N == 3))), $"Ok={low.Ok}, {nulls} null literal(s), no diagnostic\n" + string.Join("\n", low.Facts.Elements().OfType<Entity>().Where(e => Ids.Name(e.Type) is "Literal").Select(e => string.Join(",", e.Props.Items.Select(kv => Ids.Name(kv.Key) + "=" + kv.Value)))));
    }

    /// <summary>HEAD: a negative fill is a computed constant, ConstantValue is null and the series is written with no fill, as if real(null) were given.</summary>
    [Theory]
    [InlineData("0.0")]
    [InlineData("-1.0")]
    public void A_fill_of_telemetry_filled_is_written_as_given(string fill)
    {
        var low = Lower("nodes ServiceNode on service = services | project service, name, cluster, namespace;\nextend nodes ServiceNode on service = services | join kind=inner (telemetry_filled(\"master\", 30m, 5m, " + fill + ") | where property == \"flows\" | project service = id, windowStart, property, value) on service | summarize flowsOut = sum(value) by service, windowStart;\n");
        Assert.True(low.Ok, string.Join("\n", low.Diagnostics));
        var series = low.Facts.Elements().OfType<Entity>().Where(e => Ids.Name(e.Type) is "FieldRef").ToList();
        Assert.True(series.Count > 0);
        Assert.True(series.Any(e => e.Props.Items.Any(kv => Ids.Name(kv.Key) == "fill")), "no fill on the series read: " + string.Join(" | ", series.Select(e => string.Join(",", e.Props.Items.Select(kv => Ids.Name(kv.Key) + "=" + kv.Value)))));
    }
}

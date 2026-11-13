using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>Code review 2 (research 12): texts on which the front end throws, or writes operators that mean something else than Kusto binds the text to.
/// Each test fails on 4bbf5d4 for the reason its name gives.</summary>
public class KqlReviewTests
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

    /// <summary>The facts under the projection as text: type, id below the projection and properties.</summary>
    static List<string> Facts(KqlResult low) => [.. low.Facts.Elements().Select(e =>
    {
        var (id, type, props) = e switch { Entity en => (en.Id, en.Type, en.Props), Rel r => (r.Id, r.Type, r.Props), _ => ("", "", Props.Empty) };
        var cut = id.IndexOf("/projection:", StringComparison.Ordinal);
        return $"{Ids.Name(type)} {(cut >= 0 ? id[(cut + 1)..] : id)} {string.Join(", ", props.Items.Select(kv => Ids.Name(kv.Key) + "=" + kv.Value))}";
    })];

    /// <summary>Text typed in an editor is answered with diagnostics, never with an exception.</summary>
    [Theory]
    [InlineData("let __s = 1;\nnodes ServiceNode on service = services;\n")]                           // FormatException, Lowerer.Run
    [InlineData("let __s7 = services;\nnodes ServiceNode on service = services;\n")]                   // ArgumentOutOfRangeException, Lowerer.Run
    [InlineData("nodes ServiceNode on service = services | where real(1.5) > 1.0;\n")]                // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where decimal(1.5) > 1.0;\n")]             // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where real(nan) > 1.0;\n")]                // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where 1e30 > 1.0;\n")]                     // OverflowException, Lowerer.Lit
    public void Text_the_front_end_cannot_lower_gives_a_diagnostic_not_an_exception(string rest)
    {
        var thrown = Record.Exception(() => Lower(rest));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    /// <summary>Kusto: `summarize n = arg_max(a, b) by k` has the columns k, n (the greatest a) and b. The front end writes n as b of the row with the greatest a, and no column b.</summary>
    [Fact]
    public void A_named_arg_max_names_the_greatest_order_as_in_Kusto_or_is_refused()
    {
        var low = Lower("nodes ServiceNode on service = services | summarize n = arg_max(name, cluster) by service | project service, name = n, cluster = n, namespace = n;\n");
        if (!low.Ok) return;
        Assert.Contains(Facts(low), f => f.StartsWith("FieldRef ", StringComparison.Ordinal) && f.Contains("/agg:n/value:#0 ", StringComparison.Ordinal) && f.Contains("S = name ", StringComparison.Ordinal));
    }

    /// <summary>A query statement that is no step writes nothing. The author who forgot the step header gets a projection without that step and no message.</summary>
    [Theory]
    [InlineData("services | where name == \"a\";\nnodes ServiceNode on service = services;\n")]
    [InlineData("print 1;\nnodes ServiceNode on service = services;\n")]
    public void A_statement_that_writes_nothing_is_refused(string rest) => Assert.False(Lower(rest).Ok);

    /// <summary>Kusto binds a name in a function body where the function is declared. The front end resolves it where the function is called: a column of the calling rows captures a let of the same name.</summary>
    [Fact]
    public void A_let_read_in_a_scalar_function_is_the_let_not_a_column_of_the_calling_rows()
    {
        var low = Lower("let name = \"zzz\";\nlet f = (v:string) { v == name };\nnodes ServiceNode on service = services | where f(cluster);\n");
        if (!low.Ok) return;
        Assert.DoesNotContain(Facts(low), f => f.StartsWith("FieldRef ", StringComparison.Ordinal) && f.Contains("/predicate:#0/arg:#1 ", StringComparison.Ordinal) && f.Contains("S = name ", StringComparison.Ordinal));
    }

    /// <summary>The same for tables: a let that is first read inside a table function takes the function's table parameter for a let of the same name, and keeps it for every later reader.</summary>
    [Fact]
    public void A_let_read_in_a_table_function_reads_the_let_of_that_name_not_the_functions_table_parameter()
    {
        var low = Lower("let T = services | where name == \"top\";\n"
            + "let u = T | project service, name, cluster, namespace;\n"
            + "let f = (T:(service:string, name:string, cluster:string, namespace:string)) { T | join kind=inner (u | project service) on service };\n"
            + "nodes ServiceNode on service = services | where name == \"other\" | invoke f() | project service, name, cluster, namespace;\n");
        if (!low.Ok) return;
        Assert.Contains(Facts(low), f => f.StartsWith("Literal ", StringComparison.Ordinal) && f.Contains("S = top ", StringComparison.Ordinal));
    }

    /// <summary>To Kusto identity(...) is text, so a union of two identity columns checks. The front end keeps the first table's identity domain for the rows of every table.</summary>
    [Theory]
    [InlineData("let b2 = services | project g = identity(\"podName\", cluster, namespace, name), cluster, namespace;\nnodes ServiceGroup on g = union a, b2;\n")]
    [InlineData("let b2 = services | project g = name, cluster, namespace;\nnodes ServiceGroup on g = union a, b2;\n")]
    public void The_tables_of_a_union_agree_on_what_an_identity_column_is(string rest) =>
        Assert.False(Lower("let a = services | project g = identity(\"serviceGroup\", cluster, namespace), cluster, namespace;\n" + rest).Ok);

    /// <summary>Kusto: both labels hold. The front end takes the last one, so this pattern reads every Pod.</summary>
    [Fact]
    public void Two_labels_on_one_node_both_hold_or_are_refused()
    {
        var low = KqlFrontEnd.Lower("graph serviceDeps : acme.svc;\ninput master : acme.ops;\nnodes ServiceNode on service = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" and labels(s) has \"Pod\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n",
            Catalog.Value, "acme.svc");
        Assert.False(low.Ok);
    }

    /// <summary>Kusto.Language gives 0 as the value of a hexadecimal literal and of an integer beyond 64 bits, without an error; the front end writes that 0.</summary>
    [Theory]
    [InlineData("0x10", "N = 16 ")]
    [InlineData("99999999999999999999", "N = 99999999999999999999 ")]
    public void An_integer_literal_keeps_its_value_or_is_refused(string literal, string value)
    {
        var low = Lower($"nodes ServiceNode on service = services | where {literal} > 1;\n");
        if (!low.Ok) return;
        Assert.Contains(Facts(low), f => f.StartsWith("Literal ", StringComparison.Ordinal) && f.Contains("/predicate:#0/arg:#0 ", StringComparison.Ordinal) && f.Contains(value, StringComparison.Ordinal));
    }

    /// <summary>Kusto binds a call to a let of the author over the library function of the same name. The front end recognises rate, telemetry, hysteresis and identity by name and writes the engine's meaning.</summary>
    [Fact]
    public void A_let_named_like_a_library_function_is_the_authors_function_or_is_refused()
    {
        var low = Lower("let rate = (x:real) { x * 2.0 };\nnodes ServiceNode on service = services | extend r = rate(1.0) | project service, name, cluster, namespace;\n");
        if (!low.Ok) return;
        Assert.DoesNotContain(Facts(low), f => f.StartsWith("function ", StringComparison.Ordinal) && f.Contains("builtin:Rate", StringComparison.Ordinal));
    }
}

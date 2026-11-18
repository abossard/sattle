using Sattle.Core;
using Sattle.Language;

namespace Sattle.Kql.Tests;

/// <summary>Text in, definition graph and diagnostics out. The front end answers any text with operators that mean what Kusto binds the text to, or with a diagnostic: never with an exception, and never with another meaning. A test that returns when the text is refused says exactly that: refused, or right. The cases come from code review 2 (research 12) and its verifier; a comment that starts with HEAD says what the front end did before.</summary>
public class KqlTextTests
{
    static readonly string Root = FindRoot();
    static string Definitions => Path.Combine(Root, "engine", "definitions");
    static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "engine", "definitions"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    static readonly Lazy<Graph> Catalog = new(() =>
    {
        var cat = new Catalog(new KqlSource());
        var res = cat.Load(Language.Catalog.ReadDirectory(Definitions).ToList(), updatePins: true);
        Assert.DoesNotContain(res.Diagnostics, x => x.IsError);
        return cat.Graph;
    });

    const string GraphLine = "graph serviceDeps : acme.svc;\n";
    const string Services = "let services = graph(\"master\") | graph-match (s) where labels(s) has \"Service\" project service = s.id, cluster = s.cluster, namespace = s.namespace, name = s.name;\n";
    const string Head = GraphLine + "input master : acme.ops;\n" + Services;

    /// <summary>The service projection with `rest` after its graph line, its input and the let `services`.</summary>
    static KqlResult Lower(string rest) => KqlFrontEnd.Lower(Head + rest, Catalog.Value, "acme.svc");
    /// <summary>A whole text of the service projection; its input line is put after the graph line.</summary>
    static KqlResult LowerSvc(string text) => KqlFrontEnd.Lower(text.Replace(GraphLine, GraphLine + "input master : acme.ops;\n", StringComparison.Ordinal), Catalog.Value, "acme.svc");

    /// <summary>The facts under the projection as text: type, id below the projection and properties.</summary>
    static List<string> Facts(KqlResult low) => [.. low.Facts.Elements().Select(e =>
    {
        var (id, type, props) = e switch { Entity en => (en.Id, en.Type, en.Props), Rel r => (r.Id, r.Type, r.Props), _ => ("", "", Props.Empty) };
        var cut = id.IndexOf("/projection:", StringComparison.Ordinal);
        return $"{Ids.Name(type)} {(cut >= 0 ? id[(cut + 1)..] : id)} {string.Join(", ", props.Items.Select(kv => Ids.Name(kv.Key) + "=" + kv.Value))}";
    })];

    /// <summary>Text typed in an editor is answered with diagnostics, never with an exception. The last two rows are whole texts: a first token `.` is a Kusto command and the root is no query block, and an empty datatable divided by zero.</summary>
    [Theory]
    [InlineData("let __s = 1;\nnodes ServiceNode on service = services;\n")]                           // FormatException, Lowerer.Run
    [InlineData("let __s7 = services;\nnodes ServiceNode on service = services;\n")]                   // ArgumentOutOfRangeException, Lowerer.Run
    [InlineData("nodes ServiceNode on service = services | where real(1.5) > 1.0;\n")]                // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where decimal(1.5) > 1.0;\n")]             // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where real(nan) > 1.0;\n")]                // FormatException, Lowerer.Lit
    [InlineData("nodes ServiceNode on service = services | where 1e30 > 1.0;\n")]                     // OverflowException, Lowerer.Lit
    [InlineData(".show tables\ngraph serviceDeps : acme.svc;\n")]                                     // InvalidCastException, Lowerer.Run
    [InlineData("graph serviceDeps : acme.svc;\nnodes ServiceNode on service = datatable()[];\n")]    // DivideByZeroException
    public void Text_the_front_end_cannot_lower_gives_a_diagnostic_not_an_exception(string text)
    {
        var thrown = Record.Exception(() => text.Contains(GraphLine, StringComparison.Ordinal) ? LowerSvc(text) : Lower(text));
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

    /// <summary>HEAD: the Refused of WriteRules leaves Lower (after the only catch).</summary>
    [Fact]
    public void A_value_an_onDelete_row_cannot_hold_is_a_diagnostic()
    {
        KqlResult? low = null;
        var thrown = Record.Exception(() => low = LowerSvc(GraphLine + Services
            + "let retired = () { print name = datetime(2026-01-01) };\n"
            + "nodes ServiceNode on service with (rule = \"a\", accept = \"delete\", onDelete = \"retired\") = services;\n"));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
        Assert.Contains(low!.Diagnostics, d => d.IsError);
    }

    static string[] Accept(string with)
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept = \"create, delete\")", with, StringComparison.Ordinal);
        var low = KqlFrontEnd.Lower(text, Catalog.Value, "acme.biz", path);
        Assert.True(low.Diagnostics.Count == 0 || low.Diagnostics.Any(d => d.IsError), "unreachable");
        if (!low.Ok) return ["refused: " + string.Join(" | ", low.Diagnostics.Select(d => d.Message))];
        var rule = low.Facts.Elements().OfType<Entity>().First(e => e.Id.EndsWith("/rule:tree", StringComparison.Ordinal));
        var list = (ListValue)rule.Props.Get(Ids.KProp("accept"))!;
        return [.. list.Items.OfType<TextValue>().Select(t => t.S)];
    }

    /// <summary>HEAD: `accept = create, delete` unquoted gives accept = [create]; `delete` is dropped without a message.</summary>
    [Fact]
    public void An_unquoted_accept_list_keeps_both_words_or_is_refused()
    {
        var accept = Accept("with (rule = \"tree\", accept = create, delete)");
        Assert.True(accept[0].StartsWith("refused", StringComparison.Ordinal) || accept.Contains("delete"), "accept is " + string.Join(",", accept));
    }

    /// <summary>HEAD: `rule: "tree"` is no pair the pattern reads, so the step is in no rule, nothing is said, and the step has no rule fact.</summary>
    [Fact]
    public void A_setting_written_with_a_colon_is_refused_or_read()
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept = \"create, delete\")", "with (rule: \"tree\")", StringComparison.Ordinal);
        var low = KqlFrontEnd.Lower(text, Catalog.Value, "acme.biz", path);
        Assert.True(!low.Ok || low.Facts.Elements().OfType<Entity>().Any(e => e.Id.EndsWith("/rule:tree", StringComparison.Ordinal)),
            "accepted without a message, and no rule tree: " + string.Join(" | ", low.Diagnostics.Select(d => d.Message)));
    }

    /// <summary>HEAD: every call writes the body of the function again: 12 doublings give 2^12 additions.</summary>
    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(18)]
    public void Functions_that_call_the_one_before_twice_do_not_double_the_operators_with_every_line(int n)
    {
        KqlResult Chain(int m) => LowerSvc(GraphLine + Services + "let f0 = (x:long) { x + x };\n" + string.Concat(Enumerable.Range(1, m).Select(k => $"let f{k} = (x:long) {{ f{k - 1}(x) + f{k - 1}(x) }};\n"))
            + "nodes ServiceNode on service = services;\n"
            + $"extend nodes ServiceNode on service = services | summarize n = count() by service | project service, pods = f{m}(n);\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var low = Chain(n);
        // what each added line costs, beyond the text that has the step with f0 alone
        var facts = low.Facts.Elements().Count() - Chain(0).Facts.Elements().Count();
        Assert.True(!low.Ok || facts < 40 * n, $"n = {n}: {facts} facts more than with f0 alone, {watch.Elapsed.TotalSeconds:F1} s, from {n + 3} lines");
    }

    /// <summary>HEAD: a file named after a module of one part reads name[(module.Length + 1)..] of a name that is only the module.</summary>
    [Fact]
    public void A_KQL_file_of_a_module_named_in_one_part_gets_a_diagnostic_not_an_exception()
    {
        List<SourceFile> files = [new SourceFile("w.sattle", "module w @ 1.0.0;\n")];
        KqlFiles? put = null;
        var thrown = Record.Exception(() => put = KqlFiles.Of(files, [new SourceFile("w.kql", "graph p : w;\n")]));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
        Assert.False(put!.Ok);
    }

    /// <summary>HEAD: `module w;` (the parser reads a module without a version) is not found by the header pattern: "there is no module w".</summary>
    [Fact]
    public void A_module_declared_without_a_version_is_found_by_its_KQL_file()
    {
        List<SourceFile> files = [new SourceFile("w.sattle", "module w;\n")];
        var put = KqlFiles.Of(files, [new SourceFile("w.p.kql", "graph p : w;\n")]);
        Assert.DoesNotContain(put.Diagnostics, d => d.Message.Contains("there is no module w", StringComparison.Ordinal));
    }

    /// <summary>HEAD: a rule name with `#` made the printer throw (Suffix parses what follows `#`). Since 3.47 the text loads through a catalog with the KQL source, which gives no exception.</summary>
    [Fact]
    public void A_rule_name_with_a_hash_gives_no_exception()
    {
        var path = Path.Combine(Definitions, "acme.biz.capabilityView.kql");
        var text = File.ReadAllText(path).Replace("with (rule = \"tree\", accept", "with (rule = \"a#b\", accept", StringComparison.Ordinal);
        var definitions = Language.Catalog.ReadDirectory(Definitions).Select(f => f.Path == path ? new SourceFile(path, text) : f).ToList();
        var thrown = Record.Exception(() => new Language.Catalog(new KqlSource()).Load(definitions, updatePins: true));
        Assert.True(thrown is null, $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    /// <summary>HEAD: both texts lower without a message (the loop pattern to a Match whose edge has one node at both ends, `cycles=none` and a path of exactly two edges to a Traverse of depth 2 to 2 that is tested against the shortest distance).</summary>
    [Theory]
    [InlineData("graph(\"master\") | graph-match (p)-[c]->(p) where labels(c) has \"Calls\" project service = p.id, cluster = p.cluster, namespace = p.namespace, name = p.name")]
    [InlineData("graph(\"master\") | graph-match cycles=none (a)-[p*2..2]->(b) where labels(a) has \"Service\" and all(p, labels(p) has \"Calls\") project service = b.id, cluster = b.cluster, namespace = b.namespace, name = b.name")]
    public void A_graph_match_whose_meaning_the_engine_does_not_have_is_refused(string query)
    {
        var low = LowerSvc(GraphLine + "nodes ServiceNode on service = " + query + ";\n");
        Assert.False(low.Ok, "accepted without a message");
    }

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

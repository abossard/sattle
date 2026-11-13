using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Symbols;
using Sattle.Core;
using Diagnostic = Sattle.Core.Diagnostic;

namespace Sattle.Kql;

/// <summary>The facts of one projection written in KQL, and what is wrong with the text.</summary>
public sealed record KqlResult(string ProjectionId, DefBuilder Facts, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Ok => !Diagnostics.Any(d => d.IsError);
}

/// <summary>A projection written in KQL (research 10): `graph name : ontology;`, query parameters, lets, and the steps `nodes T on id = query;` and
/// `edges T from a to b = query;`. Kusto.Language parses and checks the text against a schema built from the catalog; the lowering writes the
/// operators today's text writes for the same constructs.</summary>
public static class KqlFrontEnd
{
    static readonly Regex Header = new(@"(?m)^[ \t]*graph[ \t]+(?<name>\w+)[ \t]*:[ \t]*(?<out>[\w.]+)[ \t]*;");
    static readonly Regex Untyped = new(@"(?m)^[ \t]*(?:extend[ \t]+)?(?:nodes[ \t]+on|edges[ \t]+from|link[ \t]+from)\b");
    static readonly Regex Step = new(@"(?m)^[ \t]*(?<ext>extend[ \t]+)?(?<kind>nodes|edges|link)[ \t]+(?<type>\w+)\s+(?:on\s+(?<id>\w+)|from\s+(?<src>\w+)\s+to\s+(?<dst>\w+))(?:\s+with\s*\((?<with>(?:[^()""]|""[^""]*"")*)\))?\s*=");

    /// <param name="module">the module the projection belongs to</param>
    /// <param name="inputs">the graphs the text may read, `graph("name")`, and the ontology module of each</param>
    /// <param name="rows">the tables of rows the text may read by name, and the row type of each</param>
    /// <param name="sinks">the name of the steps' operators, numbered: `emit`, or the rule that governs them</param>
    public static KqlResult Lower(string text, Graph catalog, string module, IReadOnlyDictionary<string, string> inputs, string file = "<kql>", IReadOnlyDictionary<string, string>? rows = null, string sinks = "emit")
    {
        rows ??= new Dictionary<string, string>();
        var diags = new List<Diagnostic>();
        foreach (Match m in Untyped.Matches(text))
            diags.Add(At(text, file, m.Index, "Profile", "a step names its type (nodes T on id = ..., edges T from a to b = ...): the engine writes one type per step, so a type taken from a column needs one step per type"));
        if (diags.Count > 0) return new("", new DefBuilder(), diags);
        var heads = Header.Matches(text);
        if (heads.Count != 1) return new("", new DefBuilder(), [new("Graph", "error", "", "the text names its graph once: graph <name> : <ontology>;", file, 1, 1)]);
        // the step statements become lets of the same length, so Kusto checks their queries and positions stay those of the text
        var chars = text.ToCharArray();
        Overwrite(chars, heads[0].Index, heads[0].Length, "");
        var steps = new List<StepHeader>();
        foreach (Match m in Step.Matches(text))
        {
            string? G(string n) => m.Groups[n].Success ? m.Groups[n].Value : null;
            Overwrite(chars, m.Index, m.Length, $"let __s{steps.Count} =");
            steps.Add(new StepHeader(m.Groups["ext"].Success, m.Groups["kind"].Value, m.Groups["type"].Value, G("id"), G("src"), G("dst"), G("with"), m.Index));
        }
        var code = KustoCode.ParseAndAnalyze(new string(chars) + "\nprint 0", Schema(catalog, inputs, rows));
        foreach (var d in code.GetDiagnostics().Where(d => d.Severity == "Error"))
            diags.Add(At(text, file, d.Start, "Kusto", d.Message));
        if (diags.Count > 0) return new("", new DefBuilder(), diags);
        return new Lowerer(catalog, module, inputs, rows, file, text, heads[0].Groups["name"].Value, heads[0].Groups["out"].Value, steps, code, diags, sinks).Run();
    }

    static void Overwrite(char[] chars, int at, int length, string with)
    {
        var k = 0;
        for (var i = at; i < at + length; i++)
            if (chars[i] != '\n') chars[i] = k < with.Length ? with[k++] : ' ';
    }

    internal static Diagnostic At(string text, string file, int offset, string code, string message)
    {
        var line = 1; var col = 1;
        for (var i = 0; i < Math.Min(offset, text.Length); i++) { if (text[i] == '\n') { line++; col = 1; } else col++; }
        return new Diagnostic(code, "error", "", message, file, line, col);
    }

    /// <summary>The modules a module sees: itself, the kernel and its imports, transitively.</summary>
    internal static HashSet<string> Closure(Graph g, string module)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { Ids.KernelModule };
        var d = new DefView(g); var todo = new Stack<string>([module]);
        while (todo.Count > 0)
        {
            var m = todo.Pop();
            if (!set.Add(m)) continue;
            foreach (var t in d.Targets(Ids.Module(m), "imports")) todo.Push(Ids.ModuleOf(t));
        }
        return set;
    }

    static readonly string Library = new StreamReader(typeof(KqlFrontEnd).Assembly.GetManifestResourceStream("library.kql")!).ReadToEnd();

    /// <summary>The schema Kusto checks against: one graph model per input graph whose nodes and edges have every property of its ontology,
    /// the telemetry table and the library functions.</summary>
    static ScalarSymbol Kind(string valueKind) => valueKind switch { "Text" => ScalarTypes.String, "Integer" => ScalarTypes.Long, "Decimal" => ScalarTypes.Real, "Boolean" => ScalarTypes.Bool, "Instant" => ScalarTypes.DateTime, _ => ScalarTypes.Dynamic };

    /// <summary>The fields of a row type: name and value kind.</summary>
    internal static List<(string Name, string Kind)> Fields(Graph catalog, string rowType)
    {
        var d = new DefView(catalog);
        return [.. d.Children(rowType, "field").Select(f => (d.Text(f, "name")!, Ids.Name(d.Target(f, "valueKind") ?? "")))];
    }

    static GlobalState Schema(Graph catalog, IReadOnlyDictionary<string, string> inputs, IReadOnlyDictionary<string, string> rows)
    {
        var symbols = new List<Symbol>();
        foreach (var (name, rowType) in rows)
            symbols.Add(new TableSymbol(name, Fields(catalog, rowType).Select(f => new ColumnSymbol(f.Name, Kind(f.Kind)))));
        foreach (var (g, m) in inputs)
        {
            var view = OntologyView.Build(catalog, Closure(catalog, m));
            var cols = new SortedDictionary<string, ScalarSymbol>(StringComparer.Ordinal) { ["id"] = ScalarTypes.String };
            foreach (var p in view.Props.Values)
            {
                var t = Kind(p.ValueKind);
                cols[p.Name] = cols.TryGetValue(p.Name, out var old) && old != t ? ScalarTypes.Dynamic : t;
            }
            symbols.Add(new TableSymbol("__" + g, cols.Select(c => new ColumnSymbol(c.Key, c.Value))));
            symbols.Add(new GraphModelSymbol(g, "__" + g, "__" + g, Array.Empty<string>()));
        }
        symbols.Add(new TableSymbol("Telemetry", "(graph:string, id:string, property:string, windowStart:datetime, value:real, noData:bool)"));
        foreach (Match m in Regex.Matches(Library, @"(?ms)^let (\w+) = \((.*?)\) \{\s*(.*?)\s*\};\s*$"))
            symbols.Add(new FunctionSymbol(m.Groups[1].Value, "(" + m.Groups[2].Value + ")", "{ " + m.Groups[3].Value + " }"));
        return GlobalState.Default.WithDatabase(new DatabaseSymbol("sattle", symbols));
    }
}

/// <summary>A step: `nodes T on id = q` and `edges T from a to b = q` make elements, `extend nodes ...` and `extend edges ...` add properties to elements another step makes.</summary>
sealed record StepHeader(bool Extend, string Kind, string Type, string? Id, string? Src, string? Dst, string? With, int Pos)
{
    /// <summary>The settings in `with (...)`: `key` of an edge step, `confidence` of a link; editable views come in a later stage.</summary>
    public Dictionary<string, string> Settings(out string? refusal)
    {
        refusal = null; var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = Kind switch { "edges" => "key", "link" => "confidence", _ => null };
        foreach (Match m in Regex.Matches(With ?? "", @"(\w+)\s*=\s*(""[^""]*""|'[^']*'|[^,]+)"))
        {
            var (name, value) = (m.Groups[1].Value, m.Groups[2].Value.Trim().Trim('"', '\''));
            if (name == allowed) settings[name] = value;
            else refusal ??= name is "edit" or "createKey" or "createDefaults" or "deleteSets" ? "editable views come in a later stage of the KQL front end: this stage refuses with (edit = ...)"
                : allowed is null ? $"'{name}' is not a setting of a node step: a node step has none"
                : $"'{name}' is not a setting of {(Kind == "link" ? "a link" : "an edge step")}: the one setting is {allowed} = ...";
        }
        return settings;
    }

    /// <summary>The key of an edge step: its setting, else the type name with a lower-case first letter.</summary>
    public string Key(out string? refusal) => Settings(out refusal).GetValueOrDefault("key") ?? char.ToLowerInvariant(Type[0]) + Type[1..];
}

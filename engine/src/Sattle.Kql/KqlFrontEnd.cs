using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using Sattle.Core;
using Diagnostic = Sattle.Core.Diagnostic;

namespace Sattle.Kql;

/// <summary>The facts of one projection written in KQL and what is wrong with the text, each with its place in the text.</summary>
public sealed record KqlResult(string ProjectionId, DefBuilder Facts, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Ok => !Diagnostics.Any(d => d.IsError);
    /// <summary>The place of each diagnostic, `[start, end)` in the text, in the order of `Diagnostics`.</summary>
    internal IReadOnlyList<(int Start, int End)> DiagnosticPlaces { get; init; } = [];
    /// <summary>The place each operator comes from, by operator id: a KQL operator, or a step for its sink.</summary>
    internal IReadOnlyDictionary<string, (int Start, int End)> OperatorPlaces { get; init; } = new Dictionary<string, (int, int)>();
    /// <summary>For an operator written out from a table function's body: the place of the call it was written out for.</summary>
    internal IReadOnlyDictionary<string, (int Start, int End)> OperatorCalls { get; init; } = new Dictionary<string, (int, int)>();
    /// <summary>What the lowering knows of the rows after each KQL operator, by its place.</summary>
    internal IReadOnlyDictionary<(int Start, int End), Rel> Shapes { get; init; } = new Dictionary<(int, int), Rel>();
}

/// <summary>What is wrong with a KQL text, each with its place.</summary>
sealed class Faults(string text, string file)
{
    public readonly List<Diagnostic> List = [];
    public readonly List<(int Start, int End)> Places = [];
    public int Count => List.Count;
    public void Add(int start, int end, string code, string message) { List.Add(KqlFrontEnd.At(text, file, start, code, message)); Places.Add((start, Math.Max(start, end))); }
    public void Add(SyntaxNode n, string code, string message) => Add(n.TextStart, n.End, code, message);
}

/// <summary>A KQL text as Kusto reads it: the graph line, the inputs and the steps, and the text Kusto checks, in which the graph line and the input lines
/// are blank and each step is a let of the same length, so positions are those of the text.</summary>
sealed record KqlText(string Text, Match? Head, string? Description, long History, Dictionary<string, string> Graphs, Dictionary<string, string> Rows,
    List<(string Name, string Type, int Start, int End)> InputLines, List<StepHeader> Steps, KustoCode Code, Faults Faults);

public static class KqlFrontEnd
{
    const string With = @"(?:\s+with\s*\((?<with>(?:[^()""]|""[^""]*"")*)\))?";
    static readonly Regex Header = new(@"(?m)^[ \t]*graph[ \t]+(?<name>\w+)[ \t]*:[ \t]*(?<out>[\w.]+)" + With + @"\s*;");
    static readonly Regex Input = new(@"(?m)^[ \t]*input[ \t]+(?<name>\w+)[ \t]*:[ \t]*(?<type>[\w.]+)[ \t]*;");
    static readonly Regex Untyped = new(@"(?m)^[ \t]*(?:extend[ \t]+)?(?:nodes[ \t]+on|edges[ \t]+from|link[ \t]+from)\b");
    static readonly Regex Step = new(@"(?m)^[ \t]*(?<ext>extend[ \t]+)?(?<kind>nodes|edges|link)[ \t]+(?<type>\w+)\s+(?:on\s+(?<id>\w+)|from\s+(?<src>\w+)\s+to\s+(?<dst>\w+))" + With + @"\s*=");

    /// <param name="module">the module the projection belongs to</param>
    public static KqlResult Lower(string text, Graph catalog, string module, string file = "<kql>")
    {
        var t = Read(text, catalog, module, file);
        if (t.Faults.Count > 0) return new("", new DefBuilder(), t.Faults.List) { DiagnosticPlaces = t.Faults.Places };
        return new Lowerer(catalog, module, t.Graphs, t.Rows, t.Head!.Groups["name"].Value, t.Head.Groups["out"].Value, t.Description, t.History, t.Steps, t.Code, t.Faults).Run();
    }

    internal static KqlResult Lower(KqlText t, Graph catalog, string module) =>
        new Lowerer(catalog, module, t.Graphs, t.Rows, t.Head!.Groups["name"].Value, t.Head.Groups["out"].Value, t.Description, t.History, t.Steps, t.Code, t.Faults).Run();

    /// <summary>Reads a text as far as it can, with what is wrong with it: Kusto checks the queries even when the graph line or an input is wrong.</summary>
    internal static KqlText Read(string text, Graph catalog, string module, string file = "<kql>")
    {
        var faults = new Faults(text, file);
        foreach (Match m in Untyped.Matches(text))
            faults.Add(m.Index, m.Index + m.Length, "Profile", "a step names its type (nodes T on id = ..., edges T from a to b = ...): the engine writes one type per step, so a type taken from a column needs one step per type");
        var heads = Header.Matches(text);
        if (heads.Count != 1) faults.Add(heads.Count > 1 ? heads[1].Index : 0, heads.Count > 1 ? heads[1].Index + heads[1].Length : 0, "Graph", "the text names its graph once: graph <name> : <ontology>;");
        var head = heads.Count > 0 ? heads[0] : null; string? description = null; long history = 0;
        // the graph line and the input lines become blanks, the step statements lets of the same length, so Kusto checks their queries and positions stay those of the text
        var chars = text.ToCharArray();
        if (head is not null)
        {
            foreach (var (name, value) in SettingsOf(head.Groups["with"].Value))
                if (name == "description") description = value;
                else if (name == "history" && long.TryParse(value, out var h) && h >= 0) history = h;
                else faults.Add(head.Index, head.Index + head.Length, "Graph", name == "history" ? "history is a whole number: how many inputs the graph keeps per element" : $"'{name}' is not a setting of the graph line: its settings are description and history");
            Overwrite(chars, head.Index, head.Length, "");
        }
        var steps = new List<StepHeader>();
        foreach (Match m in Step.Matches(text))
        {
            string? G(string n) => m.Groups[n].Success ? m.Groups[n].Value : null;
            Overwrite(chars, m.Index, m.Length, $"let __s{steps.Count} =");
            steps.Add(new StepHeader(m.Groups["ext"].Success, m.Groups["kind"].Value, m.Groups["type"].Value, G("id"), G("src"), G("dst"), G("with"), m.Index, m.Index + m.Length,
                m.Groups["with"].Success ? m.Groups["with"].Index : -1));
        }
        var (graphs, rows, lines) = Inputs(text, chars, catalog, module, faults);
        var code = KustoCode.ParseAndAnalyze(new string(chars) + "\nprint 0", Schema(catalog, graphs, rows));
        foreach (var d in code.GetDiagnostics().Where(d => d.Severity == "Error"))
            faults.Add(d.Start, d.End, "Kusto", d.Message);
        return new KqlText(text, head, description, history, graphs, rows, lines, steps, code, faults);
    }

    /// <summary>The graphs and tables the text reads: `input name : ontology;` for a graph, `input name : RowType;` for a table of rows. A graph the text reads
    /// by `graph("name")` alone has the ontology of that name among the module's own and the ones it imports, when exactly one has it.</summary>
    static (Dictionary<string, string> Graphs, Dictionary<string, string> Rows, List<(string Name, string Type, int Start, int End)> Lines) Inputs(string text, char[] chars, Graph catalog, string module, Faults faults)
    {
        var graphs = new Dictionary<string, string>(StringComparer.Ordinal); var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<(string Name, string Type, int Start, int End)>();
        // the modules a name may refer to: the module itself by its name, each import by its name and its alias
        var known = new List<(string Module, string? Alias)> { (module, null) };
        known.AddRange(new DefView(catalog).Out(Ids.Module(module), "imports").Select(r => (Ids.ModuleOf(r.Target), r.Props.Text(Ids.KProp("alias")))));
        string? ModuleNamed(string n) => known.Where(k => k.Module == n || k.Alias == n).Select(k => k.Module).FirstOrDefault();
        foreach (Match m in Input.Matches(text))
        {
            Overwrite(chars, m.Index, m.Length, "");
            var (name, type) = (m.Groups["name"].Value, m.Groups["type"].Value);
            lines.Add((name, type, m.Index, m.Index + m.Length));
            var dot = type.LastIndexOf('.');
            var rowType = (dot < 0 ? module : ModuleNamed(type[..dot])) is { } rm ? Ids.Child(Ids.Module(rm), "rows", type[(dot + 1)..]) : null;
            if (graphs.ContainsKey(name) || rows.ContainsKey(name)) faults.Add(m.Index, m.Index + m.Length, "Input", $"{name} is an input already");
            else if (ModuleNamed(type) is { } om) graphs[name] = om;
            else if (rowType is not null && catalog.Entity(rowType) is not null) rows[name] = rowType;
            else faults.Add(m.Index, m.Index + m.Length, "Input", $"{type} is neither an ontology nor a row type of this module or its imports ({string.Join(", ", known.Select(k => k.Module))})");
        }
        foreach (var call in KustoCode.Parse(new string(chars)).Syntax.GetDescendants<FunctionCallExpression>(f => f.Name.SimpleName == "graph"))
        {
            if (call.ArgumentList.Expressions.Count != 1 || call.ArgumentList.Expressions[0].Element.LiteralValue is not string g || graphs.ContainsKey(g)) continue;
            var named = known.Where(k => k.Module == g || k.Alias == g || k.Module[(k.Module.LastIndexOf('.') + 1)..] == g).Select(k => k.Module).Distinct().ToList();
            if (named.Count == 1) graphs[g] = named[0];
            else faults.Add(call, "Input", $"say which ontology the graph {g} has: input {g} : <ontology>; (this module imports {string.Join(", ", known.Skip(1).Select(k => k.Module))})");
        }
        return (graphs, rows, lines);
    }

    /// <summary>The settings in `with (name = value, ...)`; a line break in a quoted value, with the spaces around it, is one line break.</summary>
    internal static IEnumerable<(string Name, string Value)> SettingsOf(string? with) =>
        Regex.Matches(with ?? "", @"(\w+)\s*=\s*(""[^""]*""|'[^']*'|[^,]+)")
            .Select(m => (m.Groups[1].Value, Regex.Replace(m.Groups[2].Value.Trim().Trim('"', '\''), @"[ \t]*\r?\n[ \t]*", "\n")));

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

/// <summary>A step: `nodes T on id = q` and `edges T from a to b = q` make elements, `extend nodes ...` and `extend edges ...` add properties to elements another step makes.
/// `Pos` and `HeadEnd` are the place of the step's head, up to its `=`; `WithAt` is where the text inside `with (...)` begins, -1 without one.</summary>
sealed record StepHeader(bool Extend, string Kind, string Type, string? Id, string? Src, string? Dst, string? With, int Pos, int HeadEnd, int WithAt)
{
    /// <summary>The settings in `with (...)`: `key` of an edge step, `confidence` of a link; editable views come in a later stage.</summary>
    public Dictionary<string, string> Settings(out string? refusal)
    {
        refusal = null; var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = Kind switch { "edges" => "key", "link" => "confidence", _ => null };
        var common = new[] { "rule", "description", "accept", "update", "onCreate", "onDelete" };
        foreach (var (name, value) in KqlFrontEnd.SettingsOf(With))
        {
            if (name == allowed || common.Contains(name)) settings[name] = value;
            else refusal ??= name == "edit" ? "say what may be edited with accept = \"create, delete\" and update = \"property, ...\"; whether an edit goes back one for one or by the step's rules follows from what the step reads"
                : $"'{name}' is not a setting of {(Kind == "link" ? "a link" : Kind == "edges" ? "an edge step" : "a node step")}: its settings are {string.Join(", ", allowed is null ? common : [allowed, .. common])}";
        }
        return settings;
    }

    /// <summary>The key of an edge step: its setting, else the type name with a lower-case first letter.</summary>
    public string Key(out string? refusal) => Settings(out refusal).GetValueOrDefault("key") ?? char.ToLowerInvariant(Type[0]) + Type[1..];
}

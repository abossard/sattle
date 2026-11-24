using System.Text.Json;
using System.Text.Json.Serialization;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Gkql;

// ---- data: the program graph, as stored

/// <summary>A program: the graphs it reads, its parameters, its verbs and the wires between them. Wires carry rows with named columns; a pattern
/// variable is a column that holds an element.</summary>
public sealed record ProgramGraph(string Graph, string Ontology, string? Description, long History, List<Input> Inputs, List<Parameter> Parameters, List<VerbNode> Verbs, List<Wire> Wires);
public sealed record Input(string Name, string? Ontology, string? Rows = null);
public sealed record Parameter(string Name, string Kind, JsonElement? Default);
/// <summary>A wire from a verb's output to a named input port of another verb: `in`, or `left` and `right` of a join.</summary>
public sealed record Wire(string From, string To, string Port = "in");
public sealed record On(string Left, string Right);
/// <summary>A verb. Which fields it uses depends on `Verb`: graph-match (Graph, Pattern), where (Condition), project and extend (Columns), union,
/// summarize (Aggregates, By), join (Kind, On), nodes and edges (Target).</summary>
public sealed record VerbNode(string Id, string Verb, string? Graph, Drawing? Pattern, JsonElement? Condition, Dictionary<string, JsonElement>? Columns,
    Dictionary<string, JsonElement>? Aggregates, Dictionary<string, JsonElement>? By, string? Kind, List<On>? On, Drawing? Target,
    string? Source = null, WindowSpec? Window = null, SeriesSpec? Series = null, HysteresisSpec? Hysteresis = null, string? Rule = null, ValuesSpec? Values = null);
public sealed record ValuesSpec(Dictionary<string, string> Columns, List<Dictionary<string, JsonElement>> Rows, string? Key = null);
public sealed record WindowSpec(string Time, string Period);
public sealed record SeriesSpec(string Element, string Property, string Lookback, string? Freshness = null);
public sealed record HysteresisSpec(string[] By, string Value, decimal Above, long EnterAfter, decimal Below, long ExitAfter,
    string High, string Low, string Initial, string? NoData = null, bool EvidenceWindow = false);
/// <summary>A drawn graph: the pattern of a graph-match, or the target of a sink with placeholders (expressions over the rows) for ids and properties.</summary>
public sealed record Drawing(List<DrawnNode>? Nodes, List<DrawnEdge>? Edges);
/// <summary>`Extend`: a sink's node that adds properties to a node another sink makes.</summary>
public sealed record DrawnNode(string Var, string Label, JsonElement? Id, bool Extend, Dictionary<string, JsonElement>? Props);
public sealed record DrawnEdge(string Var, string Label, string From, string To, Dictionary<string, JsonElement>? Props);

/// <summary>A placeholder: `{"ref": "d.name"}` a column or a property of an element column, `{"lit": 0}`, `{"param": "limit"}`, `{"fn": "iff", "args": [...]}`.</summary>
abstract record Expr;
sealed record Ref(string Path) : Expr;
sealed record Lit(Value V) : Expr;
sealed record Param(string Name) : Expr;
sealed record Fn(string Name, Expr[] Args) : Expr;
sealed record Identity(string Domain, Expr[] Args) : Expr;

/// <summary>Why a program cannot be lowered, and the authored element that makes it so, below the projection id: `verb:ID` (the verb whose operators are `op:ID_1`, `op:ID_2`, ... once it lowers), `wire:FROM>TO.PORT`, `port:NAME` (the input port, a catalog element), or none for the whole program. A refused program lowers to no facts, so the focus names the authored element, not a catalog element.</summary>
sealed class Refused(string at, string message, string? element) : Exception($"{at}: {message}")
{
    /// <summary>Refused at the verb whose id is `verb`.</summary>
    public Refused(string verb, string message) : this(verb, message, "verb:" + verb) { }
    public string? Element => element;
}

/// <summary>The rows on a wire: the port that carries them, their columns, and the element type of each column that holds an element.</summary>
sealed record Rows(string Port, List<string> Cols, Dictionary<string, string> Types);

// ---- calculation: program graph to operator facts

public static class Lowering
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ProgramGraph Parse(string text) => JsonSerializer.Deserialize<ProgramGraph>(text, Json) ?? throw new JsonException("empty program");

    /// <summary>The operator facts of `program` as the projection `module/projection:graph`, or why it cannot be lowered and the element that is the cause (`Focus`: the projection, or an authored verb, wire or input of it).</summary>
    public static (string ProjectionId, IReadOnlyList<Element> Facts, string? Fault, string Focus) Lower(ProgramGraph program, Graph catalog, string module)
    {
        var w = new Writer(catalog, module, program);
        try { w.Run(); return (w.Pid, [.. w.B.Elements()], null, w.Pid); }
        catch (Refused e) { return (w.Pid, [], e.Message, e.Element is { } element ? w.Pid + "/" + element : w.Pid); }
        catch (JsonException e) { return (w.Pid, [], e.Message, w.Pid); }
    }

    internal static Value Literal(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => Value.Boolean(true),
        JsonValueKind.False => Value.Boolean(false),
        JsonValueKind.String => Value.Text(v.GetString()!),
        JsonValueKind.Number when v.TryGetInt64(out var l) => Value.Integer(l),
        JsonValueKind.Number when v.TryGetDecimal(out var number) => Value.Decimal(number),
        _ => throw new JsonException($"{v} is not a scalar literal"),
    };

    sealed class Writer(Graph catalog, string module, ProgramGraph p)
    {
        public readonly DefBuilder B = new();
        public string Pid = "";
        readonly Dictionary<string, string> graphPorts = new(StringComparer.Ordinal);
        readonly Dictionary<string, (string Port, string Type)> rowPorts = new(StringComparer.Ordinal);
        readonly Dictionary<string, OntologyView> views = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> opNo = new(StringComparer.Ordinal);

        public void Run()
        {
            var ingestion = p.Inputs.Any(i => i.Rows is not null);
            var rules = p.Verbs.Select(v => v.Rule).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            Pid = B.Contain(Ids.Module(module), "projection", p.Graph, "Projection", null,
                [("name", T(p.Graph)), ("purpose", T(ingestion ? "ingestion" : "view")),
                    ("retain", Value.Integer(p.History)), ("forward", Value.Boolean(rules.Count == 0)), .. p.Description is { } d ? [("description", T(d))] : Array.Empty<(string, Value)>()]);
            B.Edge(Pid, "outputOntology", Ids.Module(p.Ontology));
            foreach (var rule in rules)
                B.Contain(Pid, "rule", rule, "Rule", null, ("name", T(rule)), ("class", T("readonly")), ("putput", Value.Boolean(false)));
            foreach (var g in p.Inputs)
            {
                if (g.Ontology is { } ontology && g.Rows is null)
                {
                    var id = InputPort(g.Name, "graph");
                    B.Edge(id, "expects", Ids.Module(ontology));
                    if (!graphPorts.TryAdd(g.Name, id)) throw new Refused(g.Name, "duplicate graph input", "port:" + g.Name);
                }
                else if (g.Rows is { } rows && g.Ontology is null && rows.LastIndexOf('.') is > 0 and var dot)
                {
                    var type = Ids.Child(Ids.Module(rows[..dot]), "rows", rows[(dot + 1)..]);
                    if (catalog.Entity(type) is null) throw new Refused(g.Name, $"'{rows}' is not a row type", "port:" + g.Name);
                    var id = InputPort(g.Name, "rows");
                    B.Edge(id, "rowType", type);
                    if (!rowPorts.TryAdd(g.Name, (id, type))) throw new Refused(g.Name, "duplicate row input", "port:" + g.Name);
                }
                else throw new Refused(g.Name, "an input names either an ontology or a qualified row type", "port:" + g.Name);
            }
            foreach (var q in p.Parameters)
            {
                var id = B.Contain(Pid, "port", q.Name, "Port", null, [("name", T(q.Name)), ("direction", T("in")), ("kind", T("param")), .. q.Default is { } dv ? [("default", Literal(dv))] : Array.Empty<(string, Value)>()]);
                B.Edge(id, "valueKind", Ids.KBuiltin(q.Kind));
            }
            var outs = new Dictionary<string, Rows>(StringComparer.Ordinal);
            foreach (var v in Order())
            {
                var ins = p.Wires.Where(x => x.To == v.Id)
                    .Select(x => (x.Port, Rows: outs.TryGetValue(x.From, out var rows) ? rows : throw new Refused(v.Id, $"'{x.From}' has no output")))
                    .ToList();
                if (Lower(v, ins) is { } rows) outs[v.Id] = rows;
            }
        }

        string InputPort(string name, string kind) =>
            B.Contain(Pid, "port", name, "Port", null, ("name", T(name)), ("direction", T("in")), ("kind", T(kind)));

        /// <summary>The verbs in an order in which every verb comes after the verbs wired into it; a program graph has no loops.</summary>
        List<VerbNode> Order()
        {
            var ids = p.Verbs.Select(v => v.Id).ToList();
            if (ids.GroupBy(i => i, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice) throw new Refused(twice.Key, "every verb has its own id");
            foreach (var x in p.Wires.Where(x => !ids.Contains(x.From) || !ids.Contains(x.To))) throw new Refused($"{x.From} -> {x.To}", "a wire joins two verbs of the program", $"wire:{x.From}>{x.To}.{x.Port}");
            var order = new List<VerbNode>(); var done = new HashSet<string>(StringComparer.Ordinal);
            while (order.Count < p.Verbs.Count)
            {
                var next = p.Verbs.FirstOrDefault(v => !done.Contains(v.Id) && p.Wires.Where(x => x.To == v.Id).All(x => done.Contains(x.From)))
                    ?? throw new Refused(p.Graph, "the wires make a loop: a program graph has none, recursion is a bounded path on a pattern edge", null);
                order.Add(next); done.Add(next.Id);
            }
            return order;
        }

        Rows? Lower(VerbNode v, List<(string Port, Rows Rows)> ins)
        {
            if (v.Rule is not null && v.Verb is not ("nodes" or "edges")) throw new Refused(v.Id, "a rule labels a nodes or edges sink");
            Rows One() => ins is [("in", var r)] ? r : throw new Refused(v.Id, $"{v.Verb} has one wire in, to its port in");
            return v.Verb switch
            {
                "values" => ins.Count == 0 ? Values(v) : throw new Refused(v.Id, "values has no input wires"),
                "rows" => ins.Count == 0 ? SourceRows(v) : throw new Refused(v.Id, "rows reads a source directly"),
                "graph-match" => ins.Count == 0 ? Match(v) : throw new Refused(v.Id, "graph-match reads its graph, no wire goes in"),
                "where" => Filter(v.Id, One(), Read(v.Id, v.Condition ?? throw new Refused(v.Id, "where has a condition"))),
                "project" => Map(v, One(), keep: false),
                "extend" => Map(v, One(), keep: true),
                "union" => Union(v, ins),
                "summarize" => Summarize(v, One()),
                "window" => Window(v, One()),
                "series" => Series(v, One()),
                "hysteresis" => Threshold(v, One()),
                "join" => Join(v, ins),
                "nodes" => Nodes(v, One()),
                "edges" => Edges(v, One()),
                var other => throw new Refused(v.Id, $"unknown verb '{other}'"),
            };
        }

        // A typed empty table is an empty relation of the same schema. The kernel's Values
        // requires one row, so lower the empty case to a typed witness filtered by false.
        Rows Values(VerbNode v)
        {
            if (v.Values is not { Columns.Count: > 0, Rows: not null } table)
                throw new Refused(v.Id, "values needs column types and an array of rows");
            if (table.Key is { } key)
            {
                if (table.Columns.GetValueOrDefault(key) != "Text") throw new Refused(v.Id, $"key {key} must name a Text column");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cells in table.Rows)
                    if (cells is null || !cells.TryGetValue(key, out var cell) || cell.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(cell.GetString()) || !keys.Add(cell.GetString()!))
                        throw new Refused(v.Id, $"every row needs a distinct nonempty {key}");
            }
            Value Cell(string column, string type, JsonElement? json) => (type, json?.ValueKind) switch
            {
                ("Text", null or JsonValueKind.String) => T(json?.GetString() ?? ""),
                ("Integer", null) => Value.Integer(0),
                ("Integer", JsonValueKind.Number) when json.Value.TryGetInt64(out var n) => Value.Integer(n),
                ("Decimal", null) => Value.Decimal(0),
                ("Decimal", JsonValueKind.Number) when json.Value.TryGetDecimal(out var n) => Value.Decimal(n),
                ("Boolean", null or JsonValueKind.False or JsonValueKind.True) => Value.Boolean(json?.GetBoolean() ?? false),
                _ => throw new Refused(v.Id, $"column {column} needs a Text, Integer, Decimal or Boolean value of its declared type {type}"),
            };
            var op = Op("Values", v.Id);
            var rows = new Rows(P(op, "out"), [.. table.Columns.Keys.Select(c => Column(v.Id, c))], new(StringComparer.Ordinal));
            for (var index = 0; index < Math.Max(1, table.Rows.Count); index++)
            {
                var cells = table.Rows.Count == 0 ? null : table.Rows[index] ?? throw new Refused(v.Id, $"row {index + 1} needs the declared columns");
                if (cells is not null && !table.Columns.Keys.ToHashSet().SetEquals(cells.Keys))
                    throw new Refused(v.Id, $"row {index + 1} needs the declared columns");
                var row = B.Contain(op, "row", "#" + index, "RowValue", index);
                foreach (var (column, type) in table.Columns)
                {
                    var assign = B.Contain(row, "assign", column, "Assign", null, ("target", T(column)));
                    Write(v.Id, rows, new Lit(Cell(column, type, cells?[column])), assign, "value", 0);
                }
            }
            return table.Rows.Count == 0 ? Filter(v.Id, rows, new Lit(Value.Boolean(false))) : rows;
        }

        Rows SourceRows(VerbNode v)
        {
            if (v.Source is not { } source || !rowPorts.TryGetValue(source, out var input)) throw new Refused(v.Id, "rows names a row input");
            return new Rows(input.Port, [.. new DefView(catalog).Children(input.Type, "field").Select(Ids.Name)], new(StringComparer.Ordinal));
        }

        Rows Window(VerbNode v, Rows r)
        {
            if (v.Window is not { } w || !r.Cols.Contains(w.Time)) throw new Refused(v.Id, "window names a timestamp column and a period");
            var op = Op("Window", v.Id, ("timestampField", T(w.Time)), ("width", Value.Integer(Duration(v.Id, w.Period))), ("late", T("revise")));
            B.Edge(r.Port, "wire", P(op, "in"));
            return r with { Port = P(op, "out"), Cols = [.. r.Cols, "windowStart", "windowEnd"] };
        }

        Rows Series(VerbNode v, Rows r)
        {
            if (v.Series is not { } s || v.Graph is not { } g || !graphPorts.TryGetValue(g, out var port)
                || !r.Types.ContainsKey(s.Element)) throw new Refused(v.Id, "series names a graph, an element column, a metric property and a lookback");
            var op = Op("Series", v.Id, [("within", Value.Integer(Duration(v.Id, s.Lookback))),
                .. s.Freshness is { } ttl ? new[] { ("ttl", Value.Integer(Duration(v.Id, ttl))) } : []]);
            B.Edge(port, "wire", P(op, "graph"));
            B.Edge(r.Port, "wire", P(op, "in"));
            B.Contain(op, "read", "#0", "FieldRef", 0, ("path", T($"{s.Element}.{s.Property}")));
            return r with { Port = P(op, "out"), Cols = [.. r.Cols, "windowStart", "windowEnd", s.Property, .. s.Freshness is null ? Array.Empty<string>() : [TThreshold.NoDataColumn]] };
        }

        Rows Threshold(VerbNode v, Rows r)
        {
            if (v.Hysteresis is not { By.Length: > 0 } h) throw new Refused(v.Id, "hysteresis names the reading, its keys, thresholds, dwell times and states");
            var op = Op("Threshold", v.Id, [("target", T("state")), ("above", Value.Decimal(h.Above)), ("below", Value.Decimal(h.Below)),
                ("enterAfter", Value.Integer(h.EnterAfter)), ("exitAfter", Value.Integer(h.ExitAfter)), ("initial", T(h.Initial)), ("high", T(h.High)), ("low", T(h.Low)),
                .. h.NoData is { } missing ? new[] { ("nodata", T(missing)) } : [],
                .. h.EvidenceWindow ? new[] { ("evidenceWindow", Value.Boolean(true)) } : []]);
            B.Edge(r.Port, "wire", P(op, "in"));
            Write(v.Id, r, new Ref(h.Value), op, "value", 0);
            foreach (var name in h.By)
                Write(v.Id, r, new Ref(name), B.Contain(op, "key", name, "Assign", null, ("target", T(name))), "value", 0);
            return new Rows(P(op, "out"), [.. h.By, "state", .. h.EvidenceWindow ? new[] { "windowStart" } : []], r.Types.Where(kv => h.By.Contains(kv.Key)).ToDictionary());
        }

        static long Duration(string at, string duration)
        {
            try { return Lexer.DurationNs(duration); }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { throw new Refused(at, $"'{duration}' is not a duration"); }
        }

        Rows Match(VerbNode v)
        {
            if (v.Graph is not { } g || !graphPorts.TryGetValue(g, out var port)) throw new Refused(v.Id, "graph-match names a graph the program reads");
            var nodes = v.Pattern?.Nodes ?? []; var edges = v.Pattern?.Edges ?? [];
            if (nodes.Count == 0) throw new Refused(v.Id, "a pattern has at least one node");
            var view = View(p.Inputs.Single(i => i.Name == g).Ontology!);
            var op = Op("Match", v.Id);
            B.Edge(port, "wire", P(op, "graph"));
            var rows = new Rows(P(op, "out"), [], new(StringComparer.Ordinal));
            foreach (var n in nodes)
            {
                if (n.Id is not null || n.Extend) throw new Refused(v.Id, "a pattern node has a label and properties to match, no id");
                rows.Types[Column(v.Id, n.Var)] = TypeOf(v.Id, view, n.Label, rel: false);
                B.Edge(B.Contain(op, "node", n.Var, "PatternNode", null, ("var", T(n.Var))), "ofType", rows.Types[n.Var]);
                rows.Cols.Add(n.Var);
            }
            foreach (var e in edges)
            {
                if (!rows.Types.ContainsKey(e.From) || !rows.Types.ContainsKey(e.To)) throw new Refused(v.Id, $"the ends of {e.Var} are nodes of the pattern");
                if (e.From == e.To) throw new Refused(v.Id, "an edge from a node to itself: match two nodes and say they are equal in a where");
                if (e.Props is { Count: > 0 }) throw new Refused(v.Id, "a pattern edge carries no properties to match");
                var id = B.Contain(op, "edge", e.Var, "PatternEdge", null, ("var", T(e.Var)));
                B.Edge(id, "ofType", rows.Types[Column(v.Id, e.Var)] = TypeOf(v.Id, view, e.Label, rel: true));
                B.Edge(id, "from", Ids.Child(op, "node", e.From));
                B.Edge(id, "to", Ids.Child(op, "node", e.To));
                rows.Cols.Add(e.Var);
            }
            // a property drawn on a pattern node is a value it must have (a property map in a Cypher or GQL node pattern); an element without the
            // property does not match, as in Cypher: an optional property is read with a stand-in that differs from the value
            var must = new List<Expr>();
            foreach (var n in nodes)
                foreach (var (name, x) in n.Props ?? [])
                {
                    if (Read(v.Id, x) is not Lit { V: var want } lit) throw new Refused(v.Id, $"a property drawn on a pattern node is a value: {n.Var}.{name}");
                    var props = view.PropsOf(rows.Types[n.Var]);
                    var prop = props.Keys.Where(k => view.Props[k].Name == name).ToList() is [var one] ? one : throw new Refused(v.Id, $"{n.Label} has no property {name}");
                    Expr read = new Ref($"{n.Var}.{name}");
                    if (!props[prop]) read = new Fn("coalesce", [read, new Lit(Other(want))]);
                    must.Add(new Fn("==", [read, lit]));
                }
            return must.Count == 0 ? rows : Filter(v.Id, rows, must.Aggregate((a, z) => new Fn("and", [a, z])));
        }

        /// <summary>A value of the same kind that is not `v`.</summary>
        static Value Other(Value v) => v switch
        {
            BooleanValue b => Value.Boolean(!b.B),
            IntegerValue i => Value.Integer(i.N + 1),
            DecimalValue d => Value.Decimal(d.Q == decimal.MaxValue ? 0 : d.Q + 1),
            TextValue t => Value.Text(t.S + "\u0001"),
            _ => throw new Refused("pattern", $"{v} is not a value a pattern node can require", null),
        };

        Rows Filter(string at, Rows r, Expr cond)
        {
            var op = Op("Filter", at);
            Write(at, r, cond, op, "predicate", 0);
            B.Edge(r.Port, "wire", P(op, "in"));
            return r with { Port = P(op, "out") };
        }

        Rows Map(VerbNode v, Rows r, bool keep)
        {
            var cols = new List<(string Name, Expr X)>();
            if (keep) cols.AddRange(r.Cols.Select(c => (c, (Expr)new Ref(c))));
            foreach (var (name, x) in v.Columns ?? throw new Refused(v.Id, $"{v.Verb} names its columns"))
            {
                var i = cols.FindIndex(c => c.Name == Column(v.Id, name)); var e = Read(v.Id, x);
                if (i >= 0) cols[i] = (name, e); else cols.Add((name, e));
            }
            return Assigns(v.Id, r, cols);
        }

        Rows Assigns(string at, Rows r, List<(string Name, Expr X)> cols)
        {
            var op = Op("Map", at);
            for (var k = 0; k < cols.Count; k++) Write(at, r, cols[k].X, B.Contain(op, "assign", cols[k].Name, "Assign", k, ("target", T(cols[k].Name))), "value", 0);
            B.Edge(r.Port, "wire", P(op, "in"));
            return new Rows(P(op, "out"), [.. cols.Select(c => c.Name)],
                cols.Where(c => c.X is Ref { Path: var path } && r.Types.ContainsKey(path)).ToDictionary(c => c.Name, c => r.Types[((Ref)c.X).Path], StringComparer.Ordinal));
        }

        Rows Union(VerbNode v, List<(string Port, Rows Rows)> ins)
        {
            if (ins.Count < 2 || ins.Any(i => i.Port != "in")) throw new Refused(v.Id, "union has two or more wires in, to its port in");
            var first = ins[0].Rows; var acc = first;
            foreach (var (_, r0) in ins.Skip(1))
            {
                var r = r0;
                if (!r.Cols.ToHashSet().SetEquals(first.Cols)) throw new Refused(v.Id, $"the rows of a union have the same columns: {string.Join(", ", first.Cols)}");
                if (!r.Cols.SequenceEqual(first.Cols)) r = Assigns(v.Id, r, [.. first.Cols.Select(c => (c, (Expr)new Ref(c)))]);
                var op = Op("Union", v.Id);
                B.Edge(acc.Port, "wire", P(op, "in0")); B.Edge(r.Port, "wire", P(op, "in1"));
                acc = acc with { Port = P(op, "out") };
            }
            return acc;
        }

        static readonly Dictionary<string, string> Aggregates = new() { ["count"] = "count", ["sum"] = "sum", ["min"] = "min", ["max"] = "max", ["avg"] = "mean" };

        Rows Summarize(VerbNode v, Rows r)
        {
            var op = Op("Group", v.Id);
            var next = new Rows(P(op, "out"), [], new(StringComparer.Ordinal));
            foreach (var (name, x) in v.By ?? [])
            {
                if (name != "windowStart") Column(v.Id, name);
                var e = Read(v.Id, x);
                Write(v.Id, r, e, B.Contain(op, "key", name, "Assign", null, ("target", T(name))), "value", 0);
                next.Cols.Add(name);
                if (e is Ref { Path: var path } && r.Types.TryGetValue(path, out var t)) next.Types[name] = t;
            }
            foreach (var (name, x) in v.Aggregates ?? throw new Refused(v.Id, "summarize names its aggregates"))
            {
                Column(v.Id, name);
                if (Read(v.Id, x) is not Fn { Name: var fn } call || !Aggregates.TryGetValue(fn, out var engineFn)) throw new Refused(v.Id, $"an aggregate is {string.Join(", ", Aggregates.Keys)} of a value");
                var a = B.Contain(op, "agg", name, "Aggregate", null, ("target", T(name)), ("fn", T(engineFn)));
                if (call.Args is [var arg]) Write(v.Id, r, arg, a, "value", 0);
                else if (!(fn == "count" && call.Args.Length == 0)) throw new Refused(v.Id, $"{fn} takes one value");
                next.Cols.Add(name);
            }
            B.Edge(r.Port, "wire", P(op, "in"));
            return next;
        }

        Rows Join(VerbNode v, List<(string Port, Rows Rows)> ins)
        {
            if (v.Kind != "inner") throw new Refused(v.Id, "a join is kind inner");
            if (ins.Count != 2 || ins.FirstOrDefault(i => i.Port == "left").Rows is not { } left || ins.FirstOrDefault(i => i.Port == "right").Rows is not { } right)
                throw new Refused(v.Id, "a join has one wire to left and one to right");
            if (left.Cols.Intersect(right.Cols).ToList() is [_, ..] both) throw new Refused(v.Id, $"both sides have {string.Join(", ", both)}: project one side to other names");
            if (v.On is not [_, ..] on) throw new Refused(v.Id, "a join says which columns are equal");
            var eq = new List<Expr>();
            foreach (var o in on)
            {
                if (!left.Cols.Contains(o.Left) || !right.Cols.Contains(o.Right)) throw new Refused(v.Id, $"join on a column of the left side and one of the right: {o.Left}, {o.Right}");
                if (left.Types.GetValueOrDefault(o.Left) != right.Types.GetValueOrDefault(o.Right)) throw new Refused(v.Id, $"{o.Left} and {o.Right} hold different kinds of values");
                eq.Add(new Fn("==", [new Ref(o.Left), new Ref(o.Right)]));
            }
            var op = Op("Join", v.Id);
            B.Edge(left.Port, "wire", P(op, "left")); B.Edge(right.Port, "wire", P(op, "right"));
            var rows = new Rows(P(op, "out"), [.. left.Cols, .. right.Cols], new(left.Types.Concat(right.Types), StringComparer.Ordinal));
            Write(v.Id, rows, eq.Aggregate((a, z) => new Fn("and", [a, z])), op, "on", 0);
            return rows;
        }

        /// <summary>A nodes sink: its target is one node, made (or, marked extend, given more properties), its id and properties placeholders over the rows.</summary>
        Rows? Nodes(VerbNode v, Rows r)
        {
            if (v.Target is not { Nodes: [var n], Edges: null or [] }) throw new Refused(v.Id, "the target of a nodes sink is one node");
            var view = View(p.Ontology);
            var type = TypeOf(v.Id, view, n.Label, rel: false);
            var sink = Op("EmitEntity", v.Id, ("mode", T(n.Extend ? "update" : "create")));
            Govern(v, sink);
            Sink(v.Id, r, sink, type, view, n.Props);
            Write(v.Id, r, Read(v.Id, n.Id ?? throw new Refused(v.Id, "a target node has an id: a function of the match")), sink, "id", 0);
            return null;
        }

        /// <summary>An edges sink: its target is one edge between two nodes, which give only the ids of its ends.</summary>
        Rows? Edges(VerbNode v, Rows r)
        {
            if (v.Target is not { Edges: [var e], Nodes: { } ends }) throw new Refused(v.Id, "the target of an edges sink is one edge and its two end nodes");
            DrawnNode End(string var) => ends.SingleOrDefault(n => n.Var == var) is { Id: not null, Extend: false, Props: null or { Count: 0 } } n ? n
                : throw new Refused(v.Id, $"the end {var} of the edge is a node of the target with an id and nothing else");
            var (src, dst) = (End(e.From), End(e.To));
            if (ends.Count != 2) throw new Refused(v.Id, "the target of an edges sink has the two ends of its edge and no other node");
            var view = View(p.Ontology);
            var type = TypeOf(v.Id, view, e.Label, rel: true);
            if (!view.Types[type].Sources.Contains(TypeOf(v.Id, view, src.Label, rel: false)) || !view.Types[type].Targets.Contains(TypeOf(v.Id, view, dst.Label, rel: false)))
                throw new Refused(v.Id, $"{e.Label} does not go from {src.Label} to {dst.Label}");
            // the key of the relationship's id is the type's name, as a KQL edge step without a key setting has it
            var sink = Op("EmitRelationship", v.Id, ("mode", T("create")), ("key", T(char.ToLowerInvariant(e.Label[0]) + e.Label[1..])), ("retype", T("reject")));
            Govern(v, sink);
            Sink(v.Id, r, sink, type, view, e.Props);
            Write(v.Id, r, Read(v.Id, src.Id!.Value), sink, "source", 0);
            Write(v.Id, r, Read(v.Id, dst.Id!.Value), sink, "target", 0);
            return null;
        }

        void Govern(VerbNode v, string sink)
        {
            if (v.Rule is { } rule) B.Edge(Ids.Child(Pid, "rule", rule), "governs", sink);
        }

        void Sink(string at, Rows r, string sink, string type, OntologyView view, Dictionary<string, JsonElement>? props)
        {
            B.Edge(sink, "ofType", type);
            B.Edge(r.Port, "wire", P(sink, "in"));
            foreach (var (name, x) in props ?? [])
            {
                var prop = view.PropsOf(type).Keys.Where(k => view.Props[k].Name == name).ToList() is [var one] ? one : throw new Refused(at, $"{view.Types[type].Name} has no property {name}");
                var set = B.Contain(sink, "set", name, "SetProperty");
                B.Edge(set, "property", prop);
                Write(at, r, Read(at, x), set, "value", 0);
            }
        }

        // ---- names, types, expressions

        string Op(string kind, string verb, params (string, Value)[] props)
        {
            var name = $"{verb}_{opNo[verb] = opNo.GetValueOrDefault(verb) + 1}";
            var id = B.Contain(Pid, "op", name, kind, null, [("name", T(name)), .. props]);
            foreach (var (pn, dir, pk) in KernelTable.OperatorPorts(kind)) B.Contain(id, "port", pn, "Port", null, ("name", T(pn)), ("direction", T(dir)), ("kind", T(pk)));
            return id;
        }

        OntologyView View(string m)
        {
            if (views.TryGetValue(m, out var v)) return v;
            var seen = new HashSet<string>(StringComparer.Ordinal) { Ids.KernelModule }; var todo = new Stack<string>([m]); var defs = new DefView(catalog);
            while (todo.Count > 0) { var x = todo.Pop(); if (seen.Add(x)) foreach (var t in defs.Targets(Ids.Module(x), "imports")) todo.Push(Ids.ModuleOf(t)); }
            return views[m] = OntologyView.Build(catalog, seen);
        }

        static string TypeOf(string at, OntologyView view, string label, bool rel)
        {
            var hits = view.Types.Values.Where(t => t.Name == label && t.IsRel == rel).Select(t => t.Id).ToList();
            if (hits.Count > 1) hits.RemoveAll(t => Ids.ModuleOf(t) == Ids.KernelModule);
            return hits is [var one] ? one : throw new Refused(at, $"'{label}' is {(hits.Count == 0 ? "no" : "more than one")} {(rel ? "relationship" : "entity")} type");
        }

        static Expr Read(string at, JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new Refused(at, "an expression is an object");
            var fields = e.EnumerateObject().ToList();
            if (fields.Where(x => x.Name != "args").ToList() is not [var field]) throw new Refused(at, "an expression has one of ref, lit, param, fn or identity");
            var (kind, value) = (field.Name, field.Value);
            if (fields.Count > (kind is "fn" or "identity" ? 2 : 1)) throw new Refused(at, "only fn and identity take args");
            Expr[] Args() => e.TryGetProperty("args", out var args) ? [.. args.EnumerateArray().Select(a => Read(at, a))] : [];
            return kind switch
            {
                "ref" => new Ref(value.GetString()!),
                "lit" => new Lit(Literal(value)),
                "param" => new Param(value.GetString()!),
                "fn" => new Fn(value.GetString()!, Args()),
                "identity" => new Identity(value.GetString()!, Args()),
                var other => throw new Refused(at, $"'{other}' is not a placeholder"),
            };
        }

        // KQL's names for the scalar functions, and the engine's
        static readonly Dictionary<string, string> Functions = new()
        {
            ["=="] = "Equal", ["!="] = "NotEqual", ["<"] = "LessThan", ["<="] = "LessOrEqual", [">"] = "GreaterThan", [">="] = "GreaterOrEqual",
            ["and"] = "And", ["or"] = "Or", ["not"] = "Not", ["iff"] = "If", ["coalesce"] = "Coalesce",
            ["startswith"] = "StartsWith", ["endswith"] = "EndsWith", ["contains"] = "Contains",
            ["concat"] = "Concat", ["lower"] = "LowerInvariant", ["trim"] = "Trim",
        };

        void Write(string at, Rows r, Expr x, string parent, string slot, int ordinal, bool ordered = false)
        {
            var seg = "#" + ordinal; int? ord = ordered ? ordinal : null;
            switch (x)
            {
                case Ref { Path: var path }:
                    if (!r.Cols.Contains(path.Split('.')[0])) throw new Refused(at, $"the rows have no column '{path.Split('.')[0]}'");
                    if (path.Contains('.') && !r.Types.ContainsKey(path.Split('.')[0])) throw new Refused(at, $"'{path.Split('.')[0]}' holds a value, not an element with properties");
                    B.Contain(parent, slot, seg, "FieldRef", ord, ("path", T(path))); break;
                case Param { Name: var name }:
                    if (r.Cols.Contains(name)) throw new Refused(at, $"the column {name} hides the parameter {name}: give one of them another name");
                    if (!p.Parameters.Any(q => q.Name == name)) throw new Refused(at, $"'{name}' is not a parameter of the program");
                    B.Contain(parent, slot, seg, "FieldRef", ord, ("path", T(name))); break;
                case Lit { V: var v }: B.Contain(parent, slot, seg, "Literal", ord, ("value", v)); break;
                case Identity identity:
                    var defs = new DefView(catalog);
                    var domains = ProjectionChecker.Closure(catalog, p.Ontology)
                        .Select(m => Ids.Child(Ids.Module(m), "identity", identity.Domain)).Where(d => catalog.Entity(d) is not null).ToList();
                    if (domains is not [var domain]) throw new Refused(at, $"'{identity.Domain}' does not name one identity domain");
                    var components = defs.Children(domain, "component");
                    if (components.Count != identity.Args.Length) throw new Refused(at, $"'{identity.Domain}' takes {components.Count} identity components");
                    var key = B.Contain(parent, slot, seg, "IdOf", ord);
                    B.Edge(key, "domain", domain);
                    for (var i = 0; i < components.Count; i++)
                    {
                        var component = components[i];
                        var set = B.Contain(key, "component", Ids.Name(component), "SetComponent");
                        B.Edge(set, "component", component);
                        Write(at, r, identity.Args[i], set, "value", 0);
                    }
                    break;
                case Fn { Name: var fn, Args: var args }:
                    if (!Functions.TryGetValue(fn, out var builtin)) throw new Refused(at, $"'{fn}' is not a function here: {string.Join(", ", Functions.Keys)}");
                    var id = B.Contain(parent, slot, seg, "Call", ord);
                    B.Edge(id, "function", Ids.KBuiltin(builtin));
                    for (var i = 0; i < args.Length; i++) Write(at, r, args[i], id, "arg", i, true);
                    break;
            }
        }

        /// <summary>A name the program gives a column: properties read from an element travel as columns `d.name`, and windows as `windowStart`, so neither may be named.</summary>
        static string Column(string at, string name) =>
            name.Contains('.') || name.Contains('#') || name == "windowStart" ? throw new Refused(at, $"'{name}' is not a column name: no . or #, and not windowStart") : name;

        static Value T(string s) => Value.Text(s);
        static string P(string op, string port) => Ids.Child(op, "port", port);
    }
}

// ---- action at the edge: the catalog reads program files beside the KQL files

/// <summary>Graph-native projection programs and declarations.</summary>
public sealed partial class GkqlSource : IProjectionSource
{
    public const string Extension = ".gkql.json";

    public ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files)
    {
        var projections = new Dictionary<string, IReadOnlyList<Element>>(StringComparer.Ordinal);
        var places = new Dictionary<string, (string File, Pos Pos)>(StringComparer.Ordinal);
        var diags = new List<Diagnostic>();
        foreach (var f in files.Where(f => f.Path.EndsWith(Extension, StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f.Path)[..^Extension.Length];
            if (name.LastIndexOf('.') is not (> 0 and var dot) || name[..dot] != module) continue;
            var pid = Ids.Child(Ids.Module(module), "projection", name[(dot + 1)..]);
            try
            {
                var (actual, facts, fault, focus) = Lowering.Lower(Lowering.Parse(f.Text), catalog, module);
                if (fault is null && actual != pid) (fault, focus) = ($"the program names its graph {Ids.Name(actual)}, the file {Ids.Name(pid)}", pid);
                if (fault is not null) { diags.Add(new Diagnostic("Program", "error", focus, fault, f.Path, 1, 1)); continue; }
                projections[pid] = facts;
                places[pid] = (f.Path, new Pos(1, 1));
                foreach (var op in facts.OfType<Entity>().Where(e => e.Id.StartsWith(pid + "/op:", StringComparison.Ordinal)))
                    places[op.Id] = (f.Path, new Pos(1, 1));
            }
            catch (JsonException e)
            {
                diags.Add(new Diagnostic("Program", "error", pid, e.Message, f.Path, (int)(e.LineNumber ?? 0) + 1, (int)(e.BytePositionInLine ?? 0) + 1));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
            {
                diags.Add(new Diagnostic("Program", "error", pid, $"invalid program shape: {e.Message}", f.Path, 1, 1));
            }
        }
        return new ProjectionFacts(projections, places, diags);
    }

    public string? Language(string fileName) => fileName.EndsWith(Extension, StringComparison.Ordinal) && !IsModuleFile(fileName) ? "gkql" : null;
}

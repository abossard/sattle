// X12: picture C, a projection that is itself a graph (research note 14). Verbs wired as a dataflow, a drawn pattern inside each graph-match,
// a drawn target inside each sink. `ProgramSource` reads `module.projection.gkql.json` beside the KQL files and lowers it to the engine's
// existing operators (Match, Filter, Map, Union, Group, Join, EmitEntity, EmitRelationship); nothing in the engine changes.
using System.Text.Json;
using System.Text.Json.Serialization;
using Sattle.Core;
using Sattle.Language;

namespace PictureC;

// ---- data: the program graph, as stored

/// <summary>A program: the graphs it reads, its parameters, its verbs and the wires between them. Wires carry rows with named columns; a pattern
/// variable is a column that holds an element.</summary>
public sealed record ProgramGraph(string Graph, string Ontology, string? Description, long History, List<Input> Inputs, List<Parameter> Parameters, List<VerbNode> Verbs, List<Wire> Wires);
public sealed record Input(string Name, string Ontology);
public sealed record Parameter(string Name, string Kind, JsonElement? Default);
/// <summary>A wire from a verb's output to a named input port of another verb: `in`, or `left` and `right` of a join.</summary>
public sealed record Wire(string From, string To, string Port = "in");
public sealed record On(string Left, string Right);
/// <summary>A verb. Which fields it uses depends on `Verb`: graph-match (Graph, Pattern), where (Condition), project and extend (Columns), union,
/// summarize (Aggregates, By), join (Kind, On), nodes and edges (Target).</summary>
public sealed record VerbNode(string Id, string Verb, string? Graph, Drawing? Pattern, JsonElement? Condition, Dictionary<string, JsonElement>? Columns,
    Dictionary<string, JsonElement>? Aggregates, Dictionary<string, JsonElement>? By, string? Kind, List<On>? On, Drawing? Target);
/// <summary>A drawn graph: the pattern of a graph-match, or the target of a sink with placeholders (expressions over the rows) for ids and properties.</summary>
public sealed record Drawing(List<DrawnNode>? Nodes, List<DrawnEdge>? Edges);
/// <summary>`Extend`: a sink's node that adds properties to a node another sink makes.</summary>
public sealed record DrawnNode(string Var, string Label, JsonElement? Id, bool Extend, Dictionary<string, JsonElement>? Props);
public sealed record DrawnEdge(string Var, string Label, string From, string To, Dictionary<string, JsonElement>? Props);

/// <summary>A placeholder: `{"ref": "d.cluster"}` a column or a property of an element column, `{"lit": 0}`, `{"param": "minReady"}`, `{"fn": "iff", "args": [...]}`.</summary>
abstract record Expr;
sealed record Ref(string Path) : Expr;
sealed record Lit(Value V) : Expr;
sealed record Param(string Name) : Expr;
sealed record Fn(string Name, Expr[] Args) : Expr;

sealed class Refused(string at, string message) : Exception($"{at}: {message}");

/// <summary>The rows on a wire: the port that carries them, their columns, and the element type of each column that holds an element.</summary>
sealed record Rows(string Port, List<string> Cols, Dictionary<string, string> Types);

// ---- calculation: program graph to operator facts

public static class Lowering
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ProgramGraph Parse(string text) => JsonSerializer.Deserialize<ProgramGraph>(text, Json) ?? throw new JsonException("empty program");

    /// <summary>The operator facts of `program` as the projection `module/projection:graph`, or why it cannot be lowered.</summary>
    public static (string ProjectionId, IReadOnlyList<Element> Facts, string? Fault) Lower(ProgramGraph program, Graph catalog, string module)
    {
        var w = new Writer(catalog, module, program);
        try { w.Run(); return (w.Pid, [.. w.B.Elements()], null); }
        catch (Refused r) { return (w.Pid, [], r.Message); }
    }

    sealed class Writer(Graph catalog, string module, ProgramGraph p)
    {
        public readonly DefBuilder B = new();
        public string Pid = "";
        readonly Dictionary<string, string> graphPorts = new(StringComparer.Ordinal);
        readonly Dictionary<string, OntologyView> views = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> opNo = new(StringComparer.Ordinal);
        int emits;

        public void Run()
        {
            Pid = B.Contain(Ids.Module(module), "projection", p.Graph, "Projection", null,
                [("name", T(p.Graph)), ("purpose", T("view")), ("retain", Value.Integer(p.History)), ("forward", Value.Boolean(true)), .. p.Description is { } d ? [("description", T(d))] : Array.Empty<(string, Value)>()]);
            B.Edge(Pid, "outputOntology", Ids.Module(p.Ontology));
            foreach (var g in p.Inputs)
            {
                var id = B.Contain(Pid, "port", g.Name, "Port", null, ("name", T(g.Name)), ("direction", T("in")), ("kind", T("graph")));
                B.Edge(id, "expects", Ids.Module(g.Ontology));
                graphPorts[g.Name] = id;
            }
            foreach (var q in p.Parameters)
            {
                var id = B.Contain(Pid, "port", q.Name, "Port", null, [("name", T(q.Name)), ("direction", T("in")), ("kind", T("param")), .. q.Default is { } dv ? [("default", Literal(dv))] : Array.Empty<(string, Value)>()]);
                B.Edge(id, "valueKind", Ids.KBuiltin(q.Kind));
            }
            var outs = new Dictionary<string, Rows>(StringComparer.Ordinal);
            foreach (var v in Order())
            {
                var ins = p.Wires.Where(x => x.To == v.Id).Select(x => (x.Port, Rows: outs[x.From])).ToList();
                if (Lower(v, ins) is { } rows) outs[v.Id] = rows;
            }
        }

        /// <summary>The verbs in an order in which every verb comes after the verbs wired into it; a program graph has no loops.</summary>
        List<VerbNode> Order()
        {
            var ids = p.Verbs.Select(v => v.Id).ToList();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new Refused(p.Graph, "every verb has its own id");
            foreach (var x in p.Wires.Where(x => !ids.Contains(x.From) || !ids.Contains(x.To))) throw new Refused($"{x.From} -> {x.To}", "a wire joins two verbs of the program");
            var order = new List<VerbNode>(); var done = new HashSet<string>(StringComparer.Ordinal);
            while (order.Count < p.Verbs.Count)
            {
                var next = p.Verbs.FirstOrDefault(v => !done.Contains(v.Id) && p.Wires.Where(x => x.To == v.Id).All(x => done.Contains(x.From)))
                    ?? throw new Refused(p.Graph, "the wires make a loop: a program graph has none, recursion is a bounded path on a pattern edge");
                order.Add(next); done.Add(next.Id);
            }
            return order;
        }

        Rows? Lower(VerbNode v, List<(string Port, Rows Rows)> ins)
        {
            Rows One() => ins is [("in", var r)] ? r : throw new Refused(v.Id, $"{v.Verb} has one wire in, to its port in");
            return v.Verb switch
            {
                "graph-match" => ins.Count == 0 ? Match(v) : throw new Refused(v.Id, "graph-match reads its graph, no wire goes in"),
                "where" => Filter(v.Id, One(), Read(v.Id, v.Condition ?? throw new Refused(v.Id, "where has a condition"))),
                "project" => Map(v, One(), keep: false),
                "extend" => Map(v, One(), keep: true),
                "union" => Union(v, ins),
                "summarize" => Summarize(v, One()),
                "join" => Join(v, ins),
                "nodes" => Nodes(v, One()),
                "edges" => Edges(v, One()),
                var other => throw new Refused(v.Id, $"'{other}' is not a verb: graph-match, where, project, extend, union, summarize, join, nodes, edges"),
            };
        }

        Rows Match(VerbNode v)
        {
            if (v.Graph is not { } g || !graphPorts.TryGetValue(g, out var port)) throw new Refused(v.Id, "graph-match names a graph the program reads");
            var nodes = v.Pattern?.Nodes ?? []; var edges = v.Pattern?.Edges ?? [];
            if (nodes.Count == 0) throw new Refused(v.Id, "a pattern has at least one node");
            var view = View(p.Inputs.Single(i => i.Name == g).Ontology);
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
            _ => throw new Refused("pattern", $"{v} is not a value a pattern node can require"),
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
                Column(v.Id, name);
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
            Sink(v.Id, r, sink, type, view, e.Props);
            Write(v.Id, r, Read(v.Id, src.Id!.Value), sink, "source", 0);
            Write(v.Id, r, Read(v.Id, dst.Id!.Value), sink, "target", 0);
            return null;
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
            var sink = kind is "EmitEntity" or "EmitRelationship";
            var name = sink ? $"emit.{++emits}" : $"{verb}_{opNo[verb] = opNo.GetValueOrDefault(verb) + 1}";
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
            return hits is [var one] ? one : throw new Refused(at, $"'{label}' is {(hits.Count == 0 ? "no" : "more than one")} {(rel ? "relationship" : "entity")} type");
        }

        static Expr Read(string at, JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().ToList() is not [var only, ..] || e.EnumerateObject().Count() > (only.Name == "fn" ? 2 : 1))
                throw new Refused(at, $"a placeholder is {{\"ref\": ...}}, {{\"lit\": ...}}, {{\"param\": ...}} or {{\"fn\": ..., \"args\": [...]}}: {e}");
            return only.Name switch
            {
                "ref" => new Ref(only.Value.GetString()!),
                "lit" => new Lit(Literal(only.Value)),
                "param" => new Param(only.Value.GetString()!),
                "fn" => new Fn(only.Value.GetString()!, e.TryGetProperty("args", out var args) ? [.. args.EnumerateArray().Select(a => Read(at, a))] : []),
                var other => throw new Refused(at, $"'{other}' is not a placeholder"),
            };
        }

        static Value Literal(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True => Value.Boolean(true),
            JsonValueKind.False => Value.Boolean(false),
            JsonValueKind.String => Value.Text(v.GetString()!),
            JsonValueKind.Number when v.TryGetInt64(out var l) => Value.Integer(l),
            JsonValueKind.Number => Value.Decimal(v.GetDecimal()),
            _ => throw new Refused("lit", $"{v} is not a value"),
        };

        // KQL's names for the scalar functions, and the engine's
        static readonly Dictionary<string, string> Functions = new()
        {
            ["=="] = "Equal", ["!="] = "NotEqual", ["<"] = "LessThan", ["<="] = "LessOrEqual", [">"] = "GreaterThan", [">="] = "GreaterOrEqual",
            ["and"] = "And", ["or"] = "Or", ["not"] = "Not", ["iff"] = "If", ["coalesce"] = "Coalesce",
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

/// <summary>Projections in KQL (through `text`) and program graphs in `module.projection.gkql.json`.</summary>
public sealed class ProgramSource(IProjectionSource text) : IProjectionSource
{
    public const string Extension = ".gkql.json";

    public ProjectionFacts Lower(Graph catalog, string module, IReadOnlyList<SourceFile> files)
    {
        var t = text.Lower(catalog, module, files);
        var projections = new Dictionary<string, IReadOnlyList<Element>>(t.Projections, StringComparer.Ordinal);
        var places = new Dictionary<string, (string File, Pos Pos)>(t.Places, StringComparer.Ordinal);
        var diags = t.Diagnostics.ToList();
        foreach (var f in files.Where(f => f.Path.EndsWith(Extension, StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f.Path)[..^Extension.Length];
            if (name.LastIndexOf('.') is not (> 0 and var dot) || name[..dot] != module) continue;
            var (pid, facts, fault) = Lowering.Lower(Lowering.Parse(f.Text), catalog, module);
            if (fault is null && Ids.Name(pid) != name[(dot + 1)..]) fault = $"the program names its graph {Ids.Name(pid)}, the file {name[(dot + 1)..]}";
            if (fault is not null) { diags.Add(new Diagnostic("Program", "error", pid, fault, f.Path, 1, 1)); continue; }
            projections[pid] = facts;
            places[pid] = (f.Path, new Pos(1, 1));
        }
        return new ProjectionFacts(projections, places, diags);
    }

    public string? Language(string fileName) => fileName.EndsWith(Extension, StringComparison.Ordinal) ? "gkql" : text.Language(fileName);
}

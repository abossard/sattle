using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kusto.Language;
using Kusto.Language.Editor;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using Sattle.Core;
using Sattle.Language;
using Diagnostic = Sattle.Core.Diagnostic;

namespace Sattle.Kql;

/// <summary>What the editors ask of a projection's KQL text while it is written (research 11, section 2): the outline with what is wrong, completion,
/// hover, and the places that name a let, function or parameter. Each call reads the text afresh. A place is `[start, end)` in the text in UTF-16 units,
/// and every answer carries the digest of the text it read, so an answer to an older text can be dropped.</summary>
public static class KqlAuthoring
{
    /// <summary>The outline of a text and what is wrong with it. `files`, when given, are the definitions: the text is then also loaded in place of the
    /// projection and checked as the engine checks it (full depth), and the engine's errors are placed through the operators they name.</summary>
    /// <param name="projection">`module.name`, as the file `module.name.kql` names it</param>
    public static JsonObject Analyse(Catalog catalog, string projection, string text, IReadOnlyList<SourceFile>? files = null)
    {
        var (module, file) = Names(projection);
        var t = KqlFrontEnd.Read(text, catalog.Graph, module, file);
        var low = t.Faults.Count == 0 && t.Head is not null ? KqlFrontEnd.Lower(t, catalog.Graph, module) : null;
        var diags = new JsonArray();
        var list = low?.Diagnostics ?? t.Faults.List; var at = low?.DiagnosticPlaces ?? t.Faults.Places;
        for (var k = 0; k < list.Count; k++) diags.Add(Diag(text, list[k], at[k], list[k].Code == "Kusto" ? "kusto" : "profile"));
        if (files is not null && low is { Ok: true })
        {
            var put = KqlFiles.Of(files, [new SourceFile(Path.Combine(Path.GetDirectoryName(files[0].Path) ?? "", file), text)], catalog);
            if (put.Ok)
                foreach (var d in new Catalog().Load(put.Files, updatePins: true).Diagnostics.Where(d => put.Places.Keys.Any(op => Within(d.Focus, op))))
                {
                    var op = put.Places.Keys.Where(o => Within(d.Focus, o)).MaxBy(o => o.Length)!;
                    diags.Add(Diag(text, d, (put.Places[op].Start, put.Places[op].End), "engine"));
                }
        }
        return new JsonObject
        {
            ["digest"] = Digest(text), ["depth"] = files is null ? "fast" : "full", ["diagnostics"] = diags,
            ["graph"] = t.Head is { } h ? new JsonObject { ["name"] = h.Groups["name"].Value, ["ontology"] = h.Groups["out"].Value, ["place"] = P(Lead(text, h.Index), h.Index + h.Length),
                ["settings"] = Settings(h.Groups["with"].Success ? h.Groups["with"].Value : null, h.Groups["with"].Index) } : null,
            ["inputs"] = Inputs(t),
            ["parameters"] = Parameters(t.Code),
            ["functions"] = Functions(t.Code),
            ["statements"] = Statements(t, low),
        };
    }

    /// <summary>What may be written at a place: Kusto's items without what the profile refuses, and from the ontology what Kusto cannot know.</summary>
    public static JsonObject Complete(Catalog catalog, string projection, string text, int offset)
    {
        var (module, file) = Names(projection);
        var t = KqlFrontEnd.Read(text, catalog.Graph, module, file);
        var start = offset; while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-')) start--;
        var items = new JsonArray();
        var line = text[(text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1)..start];
        var ours = Ours(catalog.Graph, module, t, text, start, line).ToList();
        foreach (var (word, kind) in ours) items.Add(new JsonObject { ["text"] = word, ["kind"] = kind, ["source"] = "ontology" });
        if (ours.Count == 0 || Regex.IsMatch(line, @"=\s*$|\|\s*$"))
            foreach (var i in new KustoCodeService(t.Code).GetCompletionItems(offset).Items.Where(i => !Refused.Contains(i.DisplayText.Split('(')[0])))
                items.Add(new JsonObject { ["text"] = i.DisplayText, ["kind"] = i.Kind.ToString(), ["insert"] = (i.BeforeText ?? i.DisplayText) + i.AfterText, ["source"] = "kusto" });
        return new JsonObject { ["digest"] = Digest(text), ["replace"] = P(start, offset), ["items"] = items };
    }

    /// <summary>Kusto's description of the name at a place, and from the ontology a type's description or a property's kind, unit and role.</summary>
    public static JsonObject Hover(Catalog catalog, string projection, string text, int offset)
    {
        var (module, file) = Names(projection);
        var t = KqlFrontEnd.Read(text, catalog.Graph, module, file);
        var (s, e) = (offset, offset);
        while (s > 0 && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        while (e < text.Length && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
        var word = text[s..e]; var d = new DefView(catalog.Graph); var view = View(catalog.Graph, module, t);
        JsonObject? ont = null;
        if (view.Types.Values.Where(ty => ty.Name == word).ToList() is [var type])
            ont = new JsonObject { ["type"] = Ids.ModuleOf(type.Id) + "." + type.Name, ["relationship"] = type.IsRel, ["description"] = d.Text(type.Id, "description") };
        else if (s > 0 && text[s - 1] == '.' && view.Props.Values.Where(p => p.Name == word).ToList() is [var prop, ..])
            ont = new JsonObject { ["property"] = prop.Name, ["kind"] = prop.ValueKind, ["unit"] = prop.Unit, ["role"] = prop.Role, ["telemetry"] = prop.Temporality is not null, ["description"] = d.Text(prop.Id, "description") };
        var kusto = string.Join("\n", new KustoCodeService(t.Code).GetQuickInfo(offset).Items.Select(i => i.Text));
        return new JsonObject { ["digest"] = Digest(text), ["place"] = P(s, e), ["kusto"] = kusto.Length > 0 ? kusto : null, ["ontology"] = ont };
    }

    /// <summary>The places that name the same let, function or parameter as the name at a place, for a rename.</summary>
    public static JsonObject Related(Catalog catalog, string projection, string text, int offset)
    {
        var (module, file) = Names(projection);
        var t = KqlFrontEnd.Read(text, catalog.Graph, module, file);
        var info = new KustoCodeService(t.Code).GetRelatedElements(offset);
        return new JsonObject { ["digest"] = Digest(text), ["current"] = info.CurrentIndex, ["places"] = new JsonArray([.. info.Elements.Select(r => (JsonNode)P(r.Start, r.Start + r.Length))]) };
    }

    // ---- the outline ----

    static readonly HashSet<string> Refused = new(StringComparer.Ordinal) { "take", "limit", "sample", "sample-distinct", "sort", "order", "top", "top-nested", "top-hitters", "scan", "evaluate", "mv-expand", "mv-apply", "dcount", "count_distinct", "take_any", "now" };

    static bool Within(string id, string op) => id == op || id.StartsWith(op + "/", StringComparison.Ordinal);

    static (string Module, string File) Names(string projection) => (projection[..projection.LastIndexOf('.')], projection + ".kql");

    static string Digest(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static JsonArray P(int start, int end) => [start, end];

    /// <summary>A place begins at its own first token: the graph, input and step lines are found from the start of their line, so their indent is left out.</summary>
    static int Lead(string text, int i) { while (i < text.Length && text[i] is ' ' or '\t') i++; return i; }
    static JsonArray P(SyntaxNode n) => P(n.TextStart, n.End);

    static JsonObject Diag(string text, Diagnostic d, (int Start, int End) at, string source)
    {
        var line = 1; var col = 1;
        for (var i = 0; i < Math.Min(at.Start, text.Length); i++) { if (text[i] == '\n') { line++; col = 1; } else col++; }
        return new JsonObject { ["start"] = at.Start, ["end"] = at.End, ["line"] = line, ["column"] = col, ["severity"] = d.Severity, ["source"] = source, ["code"] = d.Code, ["message"] = d.Message };
    }

    static JsonArray Settings(string? with, int at) =>
        [.. Regex.Matches(with ?? "", @"(\w+)\s*=\s*(""[^""]*""|'[^']*'|[^,]+)").Select(m => (JsonNode)new JsonObject
            { ["name"] = m.Groups[1].Value, ["value"] = KqlFrontEnd.SettingsOf(m.Value).First().Value, ["place"] = P(at + m.Index, at + m.Index + m.Value.TrimEnd().Length) })];

    static JsonArray Inputs(KqlText t)
    {
        var all = t.Code.Syntax.GetDescendants<SyntaxNode>(n => n is FunctionCallExpression { Name.SimpleName: "graph" } || n is NameReference);
        JsonArray Uses(string name, bool graph) => [.. all.Where(n => graph ? n is FunctionCallExpression f && f.ArgumentList.Expressions is [var a] && a.Element.LiteralValue as string == name
            : n is NameReference r && r.SimpleName == name && r.Parent is not FunctionCallExpression).Select(n => (JsonNode)P(n))];
        var a = new JsonArray();
        foreach (var (name, m) in t.Graphs)
            a.Add(new JsonObject { ["name"] = name, ["kind"] = "graph", ["ontology"] = m, ["place"] = t.InputLines.Where(x => x.Name == name).Select(x => P(Lead(t.Text, x.Start), x.End)).FirstOrDefault(), ["uses"] = Uses(name, true) });
        foreach (var (name, rt) in t.Rows)
            a.Add(new JsonObject { ["name"] = name, ["kind"] = "rows", ["rowType"] = Ids.ModuleOf(rt) + "." + Ids.Name(rt), ["place"] = t.InputLines.Where(x => x.Name == name).Select(x => P(Lead(t.Text, x.Start), x.End)).FirstOrDefault(), ["uses"] = Uses(name, false) });
        return a;
    }

    static JsonObject? Parameters(KustoCode code) =>
        Top(code).OfType<QueryParametersStatement>().FirstOrDefault() is { } q ? new JsonObject
        {
            ["place"] = P(q),
            ["items"] = new JsonArray([.. q.Parameters.Select(x => x.Element).Select(p => (JsonNode)new JsonObject
                { ["name"] = p.NameAndType.Name.SimpleName, ["type"] = p.NameAndType.Type.ToString().Trim(), ["default"] = p.DefaultValue?.Value.ToString().Trim(), ["place"] = P(p) })]),
        } : null;

    static JsonArray Functions(KustoCode code) =>
        [.. Top(code).OfType<LetStatement>().Where(l => l.Expression is FunctionDeclaration).Select(l => (JsonNode)new JsonObject
        {
            ["name"] = l.Name.SimpleName,
            ["parameters"] = new JsonArray([.. ((FunctionDeclaration)l.Expression).Parameters.Parameters.Select(x => x.Element).Select(p => (JsonNode)new JsonObject
                { ["name"] = p.NameAndType.Name.SimpleName, ["table"] = p.NameAndType.Type is SchemaTypeExpression })]),
            ["place"] = P(l), ["body"] = P(((FunctionDeclaration)l.Expression).Body),
        })];

    static IEnumerable<SyntaxNode> Top(KustoCode code) => ((QueryBlock)code.Syntax).Statements.Select(s => (SyntaxNode)s.Element);

    static JsonArray Statements(KqlText t, KqlResult? low)
    {
        var lets = Top(t.Code).OfType<LetStatement>().Where(l => l.Expression is not FunctionDeclaration && !l.Name.SimpleName.StartsWith("__s", StringComparison.Ordinal)).Select(l => l.Name.SimpleName).ToHashSet(StringComparer.Ordinal);
        var functions = Top(t.Code).OfType<LetStatement>().Where(l => l.Expression is FunctionDeclaration).Select(l => l.Name.SimpleName).ToHashSet(StringComparer.Ordinal);
        var a = new JsonArray();
        foreach (var l in Top(t.Code).OfType<LetStatement>().Where(l => l.Expression is not FunctionDeclaration))
        {
            var step = l.Name.SimpleName.StartsWith("__s", StringComparison.Ordinal) ? t.Steps[int.Parse(l.Name.SimpleName[3..], System.Globalization.CultureInfo.InvariantCulture)] : null;
            var q = l.Expression; var place = step is null ? (l.TextStart, l.End) : (step.Pos, q.End);
            var names = q.GetDescendants<SyntaxNode>(n => n is NameReference or FunctionCallExpression).Prepend(q).ToList();
            JsonArray Read(Func<SyntaxNode, string?> name) => [.. names.Select(name).OfType<string>().Distinct().Order(StringComparer.Ordinal).Select(n => (JsonNode)n)];
            var o = new JsonObject
            {
                ["kind"] = step is null ? "let" : (step.Extend ? "extend " : "") + step.Kind,
                ["name"] = step is null ? l.Name.SimpleName : null, ["type"] = step?.Type, ["on"] = step?.Id, ["from"] = step?.Src, ["to"] = step?.Dst,
                ["settings"] = Settings(step?.With, step?.WithAt ?? 0),
                ["place"] = P(Lead(t.Text, place.Item1), place.Item2), ["query"] = P(q),
                ["reads"] = new JsonObject
                {
                    ["lets"] = Read(n => n is NameReference r && lets.Contains(r.SimpleName) ? r.SimpleName : null),
                    ["graphs"] = Read(n => n is FunctionCallExpression { Name.SimpleName: "graph" } f && f.ArgumentList.Expressions is [var g] ? g.Element.LiteralValue as string : null),
                    ["rows"] = Read(n => n is NameReference r && t.Rows.ContainsKey(r.SimpleName) ? r.SimpleName : null),
                    ["functions"] = Read(n => n is FunctionCallExpression f && functions.Contains(f.Name.SimpleName) ? f.Name.SimpleName : null),
                },
                ["sink"] = step is null || low is null ? null : low.OperatorPlaces.Where(kv => kv.Value == place).Select(kv => kv.Key).FirstOrDefault(),
                ["operators"] = Operators(t, low, q, step is not null, lets, functions),
            };
            a.Add(o);
        }
        return a;
    }

    /// <summary>The pipeline of a query, from its source to its last operator.</summary>
    static List<(SyntaxNode Node, Expression Result)> Chain(Expression e) => e switch
    {
        PipeExpression p => [.. Chain(p.Expression), (p.Operator, p)],
        ParenthesizedExpression p => Chain(p.Expression),
        _ => [(e, e)],
    };

    static JsonArray Operators(KqlText t, KqlResult? low, Expression query, bool isStep, HashSet<string> lets, HashSet<string> functions)
    {
        var chain = Chain(query); var a = new JsonArray();
        for (var k = 0; k < chain.Count; k++)
        {
            var (n, result) = chain[k];
            var inSink = isStep && k == chain.Count - 1 && n is ProjectOperator or ExtendOperator;
            (string Kind, string? Name) kn = n switch
            {
                NameReference r when lets.Contains(r.SimpleName) => ("let", r.SimpleName),
                NameReference r when t.Rows.ContainsKey(r.SimpleName) => ("rows", r.SimpleName),
                FunctionCallExpression { Name.SimpleName: "graph" } f => ("graph", f.ArgumentList.Expressions is [var g] ? g.Element.LiteralValue as string : null),
                FunctionCallExpression f when functions.Contains(f.Name.SimpleName) => ("invoke", f.Name.SimpleName),
                DataTableExpression => ("datatable", null),
                GraphMatchOperator => ("graph-match", null),
                FilterOperator => ("where", null),
                ProjectOperator => ("project", null),
                ExtendOperator => ("extend", null),
                DistinctOperator => ("distinct", null),
                SummarizeOperator => ("summarize", null),
                JoinOperator j => (j.Expression.GetDescendants<FunctionCallExpression>(f => f.Name.SimpleName is "telemetry" or "telemetry_filled").Any() ? "telemetry-join" : "join", null),
                UnionOperator => ("union", null),
                InvokeOperator i => ("invoke", (i.Function as FunctionCallExpression)?.Name.SimpleName),
                _ => (n.Kind.ToString(), null),
            };
            var (kind, name) = kn;
            var place = (n.TextStart, n.End);
            var shape = low?.Shapes.GetValueOrDefault(place);
            var columns = new JsonArray();
            foreach (var c in (result.ResultType as TableSymbol)?.Columns ?? [])
                columns.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.Type.Name, ["element"] = shape?.Types.GetValueOrDefault(c.Name) is { } et ? Ids.ModuleOf(et) + "." + Ids.Name(et) : null,
                    ["telemetry"] = shape is not null && (shape.Kinds.ContainsKey(c.Name) || shape.Long is { } lg && lg.Value == c.Name) ? true : null });
            var engine = low is null || inSink ? [] : low.OperatorPlaces.Where(kv => kv.Value == place && !low.OperatorCalls.ContainsKey(kv.Key)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            var expands = low is null ? [] : low.OperatorCalls.Where(kv => kv.Value.Start >= place.TextStart && kv.Value.End <= place.End).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            a.Add(new JsonObject
            {
                ["kind"] = kind, ["name"] = name, ["place"] = P(n), ["parts"] = Parts(n), ["columns"] = columns,
                ["engine"] = new JsonArray([.. engine.Select(x => (JsonNode)x)]), ["inSink"] = inSink, ["expands"] = new JsonArray([.. expands.Select(x => (JsonNode)x)]),
                // the pipelines inside: the right side of a join, the legs of a union, each from its source to its last operator
                ["inner"] = new JsonArray([.. (n switch { JoinOperator j => new[] { j.Expression }, UnionOperator u => u.Expressions.Select(x => x.Element), _ => Enumerable.Empty<Expression>() })
                    .Select(x => (JsonNode)Operators(t, low, x, false, lets, functions))]),
            });
        }
        return a;
    }

    /// <summary>An item `name = expression`, or a bare expression: the place of the item, of its name and of its expression.</summary>
    static JsonObject Item(Expression e) => e is SimpleNamedExpression s
        ? new JsonObject { ["name"] = s.Name.SimpleName, ["place"] = P(s), ["namePlace"] = P(s.Name), ["expression"] = P(s.Expression) }
        : new JsonObject { ["name"] = e is NameReference r ? r.SimpleName : null, ["place"] = P(e), ["expression"] = P(e) };

    static JsonObject Parts(SyntaxNode n) => n switch
    {
        GraphMatchOperator gm => new JsonObject { ["pattern"] = new JsonArray([.. gm.Patterns.Select(x => (JsonNode)P(x.Element))]), ["where"] = gm.WhereClause is { } w ? P(w.Condition) : null,
            ["project"] = gm.ProjectClause is { } pc ? new JsonArray([.. pc.Expressions.Select(x => (JsonNode)Item(x.Element))]) : null },
        FilterOperator f => new JsonObject { ["where"] = P(f.Condition) },
        ProjectOperator p => new JsonObject { ["project"] = new JsonArray([.. p.Expressions.Select(x => (JsonNode)Item(x.Element))]) },
        ExtendOperator x => new JsonObject { ["project"] = new JsonArray([.. x.Expressions.Select(y => (JsonNode)Item(y.Element))]) },
        DistinctOperator d => new JsonObject { ["by"] = new JsonArray([.. d.Expressions.Select(x => (JsonNode)Item(x.Element))]) },
        SummarizeOperator s => new JsonObject { ["aggregates"] = new JsonArray([.. s.Aggregates.Select(x => (JsonNode)Item(x.Element))]),
            ["by"] = new JsonArray([.. (s.ByClause?.Expressions.Select(x => x.Element) ?? []).Select(x => (JsonNode)Item(x))]) },
        JoinOperator j => new JsonObject { ["kind"] = j.Parameters.FirstOrDefault(p => p.Name.SimpleName == "kind") is { } kp ? P(kp) : null, ["right"] = P(j.Expression),
            ["on"] = j.ConditionClause is JoinOnClause on ? new JsonArray([.. on.Expressions.Select(x => (JsonNode)P(x.Element))]) : null },
        UnionOperator u => new JsonObject { ["legs"] = new JsonArray([.. u.Expressions.Select(x => (JsonNode)P(x.Element))]) },
        InvokeOperator { Function: FunctionCallExpression f } => new JsonObject { ["arguments"] = new JsonArray([.. f.ArgumentList.Expressions.Select(x => (JsonNode)P(x.Element))]) },
        FunctionCallExpression f => new JsonObject { ["arguments"] = new JsonArray([.. f.ArgumentList.Expressions.Select(x => (JsonNode)P(x.Element))]) },
        _ => new JsonObject(),
    };

    // ---- what Kusto cannot know ----

    /// <summary>The types, properties and identity domains the text may name: those of the projection's own ontology and of the graphs it reads.</summary>
    static OntologyView View(Graph catalog, string module, KqlText t)
    {
        var mods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in t.Graphs.Values.Append(t.Head?.Groups["out"].Value ?? module)) mods.UnionWith(KqlFrontEnd.Closure(catalog, m));
        return OntologyView.Build(catalog, mods);
    }

    static readonly string[] StepForms = ["nodes", "edges", "extend nodes", "extend edges", "link", "input", "let"];

    /// <summary>The items of the ontology at a place, by what the line says before it.</summary>
    static IEnumerable<(string Word, string Kind)> Ours(Graph catalog, string module, KqlText t, string text, int start, string line)
    {
        var view = View(catalog, module, t); var outModule = t.Head?.Groups["out"].Value ?? module;
        if (Regex.IsMatch(line, @"^\s*$")) return StepForms.Select(f => (f, "step"));
        if (Regex.Match(line, @"^\s*(?:extend\s+)?(nodes|edges)\s+$") is { Success: true } st)
            return OntologyView.Build(catalog, KqlFrontEnd.Closure(catalog, outModule)).Types.Values.Where(ty => !ty.Abstract && ty.IsRel == (st.Groups[1].Value == "edges") && Ids.ModuleOf(ty.Id) == outModule).Select(ty => (ty.Name, "type"));
        if (Regex.IsMatch(line, @"labels\(\w+\)\s+has\s+""$")) return view.Types.Values.Select(ty => ty.Name).Distinct().Order(StringComparer.Ordinal).Select(n => (n, "type"));
        if (Regex.IsMatch(line, @"identity\(""$"))
        {
            var mods = view.Types.Values.Select(ty => Ids.ModuleOf(ty.Id)).ToHashSet(StringComparer.Ordinal);
            return catalog.Entities.Where(e => Ids.Name(e.Type) == "IdentityDomain" && mods.Contains(Ids.ModuleOf(e.Id))).Select(e => (Ids.Name(e.Id), "identity"));
        }
        if (Regex.IsMatch(line, @"property\s*(?:==\s*""|in\s*\((?:""[^""]*""\s*,\s*)*"")$")) return view.Props.Values.Where(p => p.Temporality is not null).Select(p => p.Name).Distinct().Order(StringComparer.Ordinal).Select(n => (n, "telemetry"));
        if (Regex.IsMatch(line, @"\b(?:onCreate|onDelete)\s*=\s*""$")) return Top(t.Code).OfType<LetStatement>().Where(l => l.Expression is FunctionDeclaration).Select(l => (l.Name.SimpleName, "function"));
        if (Regex.IsMatch(line, @"\brule\s*=\s*""$")) return t.Steps.Select(s => s.Settings(out _).GetValueOrDefault("rule")).OfType<string>().Distinct().Select(r => (r, "rule"));
        if (Regex.IsMatch(line, @"\bwith\s*\((?:[^()]*,)?\s*$") && t.Steps.FirstOrDefault(s => s.Pos <= start && start <= s.HeadEnd) is { } h)
            return (h.Kind switch { "edges" => new[] { "key" }, "link" => ["confidence"], _ => [] }).Concat(["rule", "description", "accept", "update", "onCreate", "onDelete"]).Select(s => (s, "setting"));
        // the columns of a step's query after on, from and to
        if (Regex.IsMatch(line, @"^\s*(?:extend\s+)?(?:nodes|edges|link)\s+\w+\s+(?:on|from|.*\bto)\s+$")
            && Top(t.Code).OfType<LetStatement>().FirstOrDefault(l => t.Steps.FindIndex(s => s.Pos <= start && start <= s.HeadEnd) is var k && k >= 0 && l.Name.SimpleName == $"__s{k}") is { Expression.ResultType: TableSymbol ts })
            return ts.Columns.Select(c => (c.Name, "column"));
        return [];
    }
}

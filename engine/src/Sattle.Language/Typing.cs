using System.Text;
using Sattle.Core;

namespace Sattle.Language;

public enum Tag { Text, Integer, Decimal, Boolean, Instant, Ent, Rel, List, Null }

/// <summary>Kinds of research 03 section 3.2: scalars with units, entity and relationship references, lists.</summary>
public sealed record Ty(Tag Tag, string Unit = "1", string? TypeId = null, Tag? Item = null)
{
    public static readonly Ty Text = new(Tag.Text), Boolean = new(Tag.Boolean), Instant = new(Tag.Instant), Null = new(Tag.Null);
    public static Ty Int(string unit = "1") => new(Tag.Integer, unit);
    public static Ty Dec(string unit = "1") => new(Tag.Decimal, unit);
    public static Ty Ent(string? type) => new(Tag.Ent, "1", type);
    public static Ty Rel(string? type) => new(Tag.Rel, "1", type);
    public bool IsNumeric => Tag is Tag.Integer or Tag.Decimal;
    public override string ToString() => Tag switch
    {
        Tag.Integer or Tag.Decimal => Unit == "1" ? Tag.ToString() : $"{Tag}<{Unit}>",
        Tag.Ent or Tag.Rel => $"{Tag}<{(TypeId is null ? "any" : Ids.Name(TypeId))}>",
        Tag.List => $"List<{Item}>",
        _ => Tag.ToString(),
    };
}

/// <summary>Field role: graph data or telemetry with temporality and period (ns).</summary>
public sealed record Role(string? Temporality, long Period)
{
    public static readonly Role Data = new((string?)null, 0);
    public bool IsData => Temporality is null;
    public static Role Tele(string temporality, long period) => new(temporality, period);
    public override string ToString() => IsData ? "data" : $"tele<{Temporality}, {Lexer.DurationText(Period)}>";
}

/// <summary>Field type φ = (κ, π, ρ) plus the combine provenance of roll-up outputs (section 3.2, EmitEntity).</summary>
public sealed record FType(Ty Ty, bool Req, Role Role, string? Prov = null)
{
    public override string ToString() => $"({Ty}, {(Req ? "req" : "opt")}, {Role})";
}

/// <summary>A column of a row type. Origin identifies the Match variable an entity or relationship column comes from.</summary>
public sealed record Col(string Name, FType T, string? Origin = null);

public sealed class Schema(IReadOnlyList<Col> cols, long window = 0)
{
    readonly Dictionary<string, Col> by = cols.ToDictionary(c => c.Name, StringComparer.Ordinal);
    public IReadOnlyList<Col> Cols { get; } = cols;
    public long Window { get; } = window;
    public Col? Find(string name) => by.GetValueOrDefault(name);
    public bool Has(string name) => by.ContainsKey(name);
    public Schema With(IEnumerable<Col> add, long? window = null) => new([.. Cols, .. add], window ?? Window);
    public Schema Replace(string name, Func<Col, Col> f) => new([.. Cols.Select(c => c.Name == name ? f(c) : c)], Window);
    public override string ToString() => "{" + string.Join(", ", Cols.Select(c => $"{c.Name}: {c.T}")) + "}";
}

// ---- typed expressions and operators (the decoded form of a definition graph) ----------------------------

public abstract record TExpr(FType Type);
public sealed record TField(string Name, FType Type) : TExpr(Type);
public sealed record TLit(Value Value, FType Type) : TExpr(Type);
public sealed record TCall(string Fn, IReadOnlyList<TExpr> Args, FType Type) : TExpr(Type);
public sealed record TComp(string CompId, string CompName, string Canonicalizer, TExpr Value);
public sealed record TId(string DomainId, string DomainName, IReadOnlyList<TComp> Comps, FType Type) : TExpr(Type);

public sealed record TAgg(string Target, string Fn, TExpr? Value, TExpr? Weight, FType Out);
public sealed record TSet(string PropId, string PropName, TExpr Value, bool Telemetry, long Period, string? Combine);
public sealed record Src(bool IsPort, string Name);

public abstract record TOp(string Name, string Id, string Kind, IReadOnlyDictionary<string, Src> Inputs, Schema Out);
public sealed record TNode(string Var, string TypeId);
public sealed record TEdge(string? Var, string TypeId, string From, string To);
public sealed record TRead(string Var, string PropId, string Col);
public sealed record TMatch(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<TNode> Nodes, IReadOnlyList<TEdge> Edges, IReadOnlyList<TRead> Reads, string GraphPort)
    : TOp(Name, Id, "Match", Inputs, Out);
public sealed record TUnion(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string Conflict) : TOp(Name, Id, "Union", Inputs, Out);
public sealed record TJoin(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<(TExpr Left, TExpr Right)> Keys, TExpr? Residual) : TOp(Name, Id, "Join", Inputs, Out);
public sealed record TTraverse(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string GraphPort, string RelTypeId, string Direction, long Min, long Max, string Start, string Target, IReadOnlyList<TRead> Reads) : TOp(Name, Id, "Traverse", Inputs, Out);
public sealed record TFixpoint(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, long MaxIterations, string OverPort) : TOp(Name, Id, "Fixpoint", Inputs, Out);
public sealed record TValues(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<Value[]> Rows) : TOp(Name, Id, "Values", Inputs, Out);
public sealed record TDistinct(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out) : TOp(Name, Id, "Distinct", Inputs, Out);
public sealed record TFilter(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, TExpr Pred) : TOp(Name, Id, "Filter", Inputs, Out);
public sealed record TMap(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<(string Name, TExpr Value)> Assigns) : TOp(Name, Id, "Map", Inputs, Out);
public sealed record TGroup(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<(string Name, TExpr Value)> Keys, IReadOnlyList<TAgg> Aggs, bool ClosesWindows)
    : TOp(Name, Id, "Group", Inputs, Out);
public sealed record TRetain(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string TimeField, long AgeNs) : TOp(Name, Id, "Retain", Inputs, Out);
public sealed record TWindow(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string TimeField, long WidthNs, string Late) : TOp(Name, Id, "Window", Inputs, Out);
public sealed record TSeriesRead(string Var, string PropId, string OutName, string ValueKind);
public sealed record TSeries(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string GraphPort, IReadOnlyList<TSeriesRead> Reads, long WithinNs, long PeriodNs, string TargetVar)
    : TOp(Name, Id, "Series", Inputs, Out);
public sealed record TRollup(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, IReadOnlyList<(string Name, TExpr Value)> Keys, IReadOnlyList<TAgg> Aggs, long PeriodNs)
    : TOp(Name, Id, "Rollup", Inputs, Out);
public sealed record TThreshold(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, TExpr Value, IReadOnlyList<(string Name, TExpr Value)> Keys, string Target,
    decimal Above, long EnterAfter, string High, decimal Below, long ExitAfter, string Low, string Initial,
    TExpr? AboveBy = null, TExpr? EnterBy = null, TExpr? BelowBy = null, TExpr? ExitBy = null) : TOp(Name, Id, "Threshold", Inputs, Out);
public sealed record TEmitEntity(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string TypeId, bool Update, TExpr EntityId, IReadOnlyList<TSet> Sets, string? Rule)
    : TOp(Name, Id, "EmitEntity", Inputs, Out);
public sealed record TEmitRel(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string TypeId, bool Update, string Key, TExpr Source, TExpr Target, IReadOnlyList<TSet> Sets, string Retype, string? Rule)
    : TOp(Name, Id, "EmitRelationship", Inputs, Out);
public sealed record TLink(string Name, string Id, IReadOnlyDictionary<string, Src> Inputs, Schema Out, string PolicyId, string PolicyName, TId Left, TId Right, decimal Confidence, string Mode, decimal Threshold, string? Rule)
    : TOp(Name, Id, "Link", Inputs, Out);

public sealed record TPort(string Name, string Id, string Kind, Schema? Rows, string? RowTypeId, string? ExpectsModule);
/// <summary>Edit policies of a lens rule: accepted edits, the create key and defaults (the complement's "missing"), and what deleting sets.</summary>
public sealed record TPolicy(IReadOnlyList<string> Accept, string? CreateDomainId, string? CreateDomainName, string? SourceTypeId,
    IReadOnlyList<(string PropId, string Name, TExpr Value)> Defaults, string? DeleteMode, IReadOnlyList<(string PropId, string Name, TExpr Value)> DeleteSets);
public sealed record TRule(string Name, string Class, string? Reason, IReadOnlyList<string> Sinks, TPolicy? Policy = null, bool PutPut = false);

public sealed class TypedProjection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<(string Name, string Kind, Value? Default)> Params { get; init; } = [];
    public required string Module { get; init; }
    public required string Purpose { get; init; }
    public required bool Forward { get; init; }
    public required int Retain { get; init; }
    public required string OutputModule { get; init; }
    public required IReadOnlyList<TPort> Ports { get; init; }
    public required IReadOnlyList<TOp> Ops { get; init; } // topological order
    public required IReadOnlyList<TRule> Rules { get; init; }
    /// <summary>The lift statements of the projection and the operators each was lowered to (they are in Ops like any other).</summary>
    public IReadOnlyList<TLiftInfo> Lifts { get; init; } = [];
    public TPort Port(string name) => Ports.First(p => p.Name == name);
}

public sealed record TSource(string Name, string Adapter, string RowTypeId);
public sealed record TGraph(string Name, string ProjectionId, bool Master, IReadOnlyDictionary<string, string> Bindings /* port -> source or graph name */, IReadOnlyDictionary<string, Value>? Params = null, TypedProjection? Instance = null);
public sealed class TypedSpace
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<TSource> Sources { get; init; }
    public required IReadOnlyList<TGraph> Graphs { get; init; }
}

/// <summary>Subkinding, role join and unit algebra (section 3.2, 10.2).</summary>
public static class Types
{
    public static bool Sub(Ty a, Ty b, OntologyView? v)
    {
        if (a.Tag == Tag.Null) return true;
        if (a.Tag != b.Tag) return false;
        switch (a.Tag)
        {
            case Tag.Integer or Tag.Decimal: return a.Unit == b.Unit;
            case Tag.Ent or Tag.Rel: return b.TypeId is null || (a.TypeId is not null && v is not null && v.IsSubtype(a.TypeId, b.TypeId)) || a.TypeId == b.TypeId;
            case Tag.List: return a.Item == b.Item;
            default: return true;
        }
    }

    public static bool Comparable(Ty a, Ty b, OntologyView? v) => Sub(a, b, v) || Sub(b, a, v);

    public static string UnitMul(string a, string b) => a == "1" ? b : b == "1" ? a : a + "*" + b;
    public static string UnitDiv(string a, string b) => b == "1" ? a : a == b ? "1" : (a == "1" ? "1/" + b : a + "/" + b);

    /// <summary>Role of a computed value. Returns null and an error when the combination is not allowed.</summary>
    public static Role? Join(string fn, IReadOnlyList<Role> roles, out string? error)
    {
        error = null;
        var tele = roles.Where(r => !r.IsData).ToList();
        if (tele.Count == 0) return Role.Data;
        if (tele.Any(r => r.Temporality == "cumulative") && fn is not ("Equal" or "NotEqual" or "IsPresent" or "Coalesce"))
        { error = "arithmetic on a cumulative value is an error, apply Delta first"; return null; }
        if (tele.Select(r => r.Period).Distinct().Count() > 1) { error = "telemetry operands have different periods"; return null; }
        var p = tele[0].Period;
        var allTele = roles.All(r => !r.IsData);
        switch (fn)
        {
            case "Add" or "Subtract":
                if (!allTele) { error = "telemetry and data cannot be added or subtracted"; return null; }
                if (tele.Select(r => r.Temporality).Distinct().Count() > 1) { error = "operands have different temporality"; return null; }
                return tele[0];
            case "Multiply":
                if (allTele) { error = "product of two telemetry values is not defined"; return null; }
                return tele[0];
            case "Divide":
                if (allTele)
                {
                    if (tele[0].Temporality == tele[1].Temporality) return Role.Tele("gauge", p);
                    error = "ratio of values with different temporality"; return null;
                }
                if (!roles[0].IsData) return tele[0];
                error = "a data value divided by a telemetry value is not defined"; return null;
            case "Negate": return tele[0];
            default: // comparisons, logic, Equal, Coalesce, IsPresent
                return tele.Select(r => r.Temporality).Distinct().Count() > 1 ? Role.Tele("gauge", p) : tele[0];
        }
    }
}

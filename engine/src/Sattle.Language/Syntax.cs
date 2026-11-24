using Sattle.Core;

namespace Sattle.Language;

public readonly record struct Pos(int Line, int Col)
{
    public override string ToString() => $"{Line}:{Col}";
}

/// <summary>Typed parse error with a position (the "check" command reports these).</summary>
public sealed class SyntaxError(string message, Pos pos) : Exception(message) { public Pos Pos { get; } = pos; }

// ---- AST --------------------------------------------------------------------------------------------------

public sealed record Ref(string? Alias, string Name, Pos Pos)
{
    public override string ToString() => Alias is null ? Name : Alias + "." + Name;
}
public sealed record Import(string Module, string Version, string? Digest, string Alias, Pos Pos, Pos DigestPos);
public sealed record VType(string Kind, string? Item, Ref? Target, Pos Pos);

/// <summary>What a relationship type says about its two ends, declared with `kind` and served with the ontology for a viewer to draw and filter by. The engine reads none of it. `communication`: the source was observed talking to the target. `composition`: one end is a part of the other or is carried by it (which end is the whole is the type's own matter). `dependency`: the source needs the target to work, so a failure of the target reaches the source. `support`: the source is a record about the target (evidence, a measurement, a rule) and no part of the modelled system.</summary>
public static class RelationshipKinds
{
    public static readonly string[] All = ["communication", "composition", "dependency", "support"];
}
/// <summary>Tree presentation metadata. A count carries the exact state label declared by the ontology.</summary>
public static class TreeRoles
{
    public const string CountPrefix = "count:";
    public static bool IsStructural(string role) =>
        role is "own" or "rolledUp" or "severity" or "kind" or "curatedBy" or "critical" or "hidden" or "mode" or "share";
    public static string? CountLabel(string role) => role.StartsWith(CountPrefix, StringComparison.Ordinal) ? role[CountPrefix.Length..] : null;
}
public sealed record Member(string Name, string? Was, bool Optional, VType Type, bool Nullable, string? Unit, TeleMod? Tele, string? Doc, Pos Pos, List<(Value Value, string Role)>? Roles = null, string? TreeRole = null, bool Label = false);
public sealed record TeleMod(string Temporality, string Combine, long PeriodNs);

public abstract record Decl(string? Doc, Pos Pos);
public sealed record EntityDecl(bool Abstract, string Name, string? Was, List<Ref> Supers, List<Member> Members, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record RelDecl(bool Abstract, string Name, string? Was, Ref? Super, List<Ref> Sources, List<Ref> Targets, List<Member> Members, string? Doc, Pos Pos, bool Hierarchy = false, string? Kind = null, bool Rollup = false) : Decl(Doc, Pos);
public sealed record ExtendDecl(Ref Target, List<Member> Members, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record ConstraintDecl(string? Name, string Kind, Ref Target, string? Prop, string[] Values, decimal? Min, decimal? Max, string? Pattern, string? Side, long CardMin, long CardMax, string[] KeyProps, bool Warning, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record KeyComp(string Name, string Kind, string? Canonicalizer, Pos Pos);
public sealed record IdentityDecl(string Name, Ref? For, string Version, List<KeyComp> Components, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record EquivalenceDecl(string Name, Ref A, Ref B, string Mode, decimal Threshold, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record FieldDecl(string Name, bool Optional, VType Type, string? Unit, Pos Pos);
public sealed record RowsDecl(string Name, List<FieldDecl> Fields, string? Doc, Pos Pos) : Decl(Doc, Pos);
public sealed record SpaceSource(string Name, string Adapter, Ref Rows, Pos Pos);
public sealed record SpaceGraph(string Name, bool Master, Ref Projection, List<(string Port, string From, Value? Literal)> Args, Pos Pos);
public sealed record SpaceDecl(string Name, List<SpaceSource> Sources, List<SpaceGraph> Graphs, string? Doc, Pos Pos) : Decl(Doc, Pos);

public sealed record ModuleAst(string Name, string Version, string? Doc, List<Import> Imports, List<Decl> Decls, Pos Pos);

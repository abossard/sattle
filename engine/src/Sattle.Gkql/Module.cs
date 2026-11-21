using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;
using TypeRef = Sattle.Language.Ref;

namespace Sattle.Gkql;

public sealed partial class GkqlSource
{
    public const string ModuleExtension = ".module.gkql.json";
    static readonly Pos At = new(1, 1);

    public bool IsModuleFile(string fileName) => fileName.EndsWith(ModuleExtension, StringComparison.Ordinal);

    public ModuleAst ReadModule(SourceFile file)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ModuleDocument>(file.Text, Lowering.Json) ?? throw new JsonException("empty module");
            var name = Path.GetFileName(file.Path)[..^ModuleExtension.Length];
            if (m.Module != name) throw new JsonException($"module is named {m.Module}, the file is named {name}");
            var imports = (m.Imports ?? []).Select(i => new Import(i.Module, i.Version, i.Digest, i.Alias, At, At)).ToList();
            var declarations = new List<Decl>();
            declarations.AddRange((m.Entities ?? []).Select(e =>
                new EntityDecl(false, e.Name, null, [], Members(e.Properties), null, At)));
            declarations.AddRange((m.Relationships ?? []).Select(e =>
                new RelDecl(false, e.Name, null, null, [Reference(e.From)], [Reference(e.To)], Members(e.Properties), null, At, e.Hierarchy, e.Kind, e.Rollup)));
            declarations.AddRange((m.Identities ?? []).Select(i => new IdentityDecl(i.Name, Reference(i.Type), "1",
                i.Components.Select(c => new KeyComp(c.Name, c.Type, c.Normalize, At)).ToList(), null, At)));
            declarations.AddRange((m.Rows ?? []).Select(r => new RowsDecl(r.Name,
                r.Fields.Select(f => new FieldDecl(f.Name, f.Optional, ValueType(f.Type), null, At)).ToList(), null, At)));
            declarations.AddRange((m.Constraints ?? []).Select(c => new ConstraintDecl(null, c.Kind, Reference(c.Type), null,
                [], null, null, null, c.Side, c.Min, c.Max ?? -1, [], false, null, At)));
            if (m.Space is { } s)
                declarations.Add(new SpaceDecl(s.Name,
                    s.Sources.Select(x => new SpaceSource(x.Name, x.Adapter, Reference(x.Rows), At)).ToList(),
                    s.Graphs.Select(x => new SpaceGraph(x.Name, x.Master, Reference(x.Projection),
                        x.Inputs.Select(kv => (kv.Key, kv.Value, (Value?)null)).ToList(), At)).ToList(),
                    null, At));
            return new ModuleAst(m.Module, m.Version, null, imports, declarations, At);
        }
        catch (JsonException e)
        {
            throw new SyntaxError(e.Message, new Pos((int)(e.LineNumber ?? 0) + 1, (int)(e.BytePositionInLine ?? 0) + 1));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new SyntaxError($"invalid GKQL module: {e.Message}", At);
        }
    }

    public string? RebindPins(SourceFile file, IReadOnlyDictionary<string, string> pins)
    {
        if (!IsModuleFile(file.Path)) return null;
        var root = JsonNode.Parse(file.Text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        foreach (var import in root["imports"]!.AsArray())
            if (pins.TryGetValue(import!["module"]!.GetValue<string>(), out var digest)) import["digest"] = digest;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    static TypeRef Reference(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? new TypeRef(null, name, At) : new TypeRef(name[..dot], name[(dot + 1)..], At);
    }

    static VType ValueType(string kind) => new(kind, null, null, At);

    static List<Member> Members(List<PropertyDocument>? properties) => (properties ?? []).Select(p =>
        new Member(p.Name, null, p.Optional, ValueType(p.Type), false, null,
            p.Telemetry is { } t ? new TeleMod(t.Temporality, t.Combine, Lexer.DurationNs(t.Period)) : null, null, At,
            p.Roles?.Select(kv => (Value.Text(kv.Key), kv.Value)).ToList(), PropertyTreeRole(p), p.Label)).ToList();

    static string? PropertyTreeRole(PropertyDocument p)
    {
        if (p.TreeCount is null) return p.TreeRole;
        if (p.TreeRole is not null || string.IsNullOrWhiteSpace(p.TreeCount))
            throw new JsonException($"{p.Name}: treeCount requires one state label and cannot be combined with treeRole");
        return TreeRoles.CountPrefix + p.TreeCount;
    }

    sealed record ModuleDocument(string Module, string Version, List<ImportDocument>? Imports, List<EntityDocument>? Entities,
        List<RelationshipDocument>? Relationships, List<IdentityDocument>? Identities, List<RowsDocument>? Rows,
        List<ConstraintDocument>? Constraints, SpaceDocument? Space);
    sealed record ImportDocument(string Module, string Alias, string Version, string? Digest);
    sealed record EntityDocument(string Name, List<PropertyDocument>? Properties);
    sealed record PropertyDocument(string Name, string Type, bool Optional, bool Label, TelemetryDocument? Telemetry,
        string? TreeRole, string? TreeCount, Dictionary<string, string>? Roles);
    sealed record TelemetryDocument(string Temporality, string Combine, string Period);
    sealed record RelationshipDocument(string Name, string From, string To, string? Kind, bool Hierarchy, bool Rollup, List<PropertyDocument>? Properties);
    sealed record IdentityDocument(string Name, string Type, List<ComponentDocument> Components);
    sealed record ComponentDocument(string Name, string Type, string? Normalize);
    sealed record RowsDocument(string Name, List<FieldDocument> Fields);
    sealed record FieldDocument(string Name, string Type, bool Optional);
    sealed record ConstraintDocument(string Kind, string Type, string? Side, long Min, long? Max);
    sealed record SpaceDocument(string Name, List<SourceDocument> Sources, List<GraphDocument> Graphs);
    sealed record SourceDocument(string Name, string Adapter, string Rows);
    sealed record GraphDocument(string Name, string Projection, bool Master, Dictionary<string, string> Inputs);
}

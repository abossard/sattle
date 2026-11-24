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
                new RelDecl(false, e.Name, null, e.Super is { } super ? Reference(super) : null, [Reference(e.From)], [Reference(e.To)], Members(e.Properties), null, At, e.Hierarchy, e.Kind, e.Rollup)));
            declarations.AddRange((m.Identities ?? []).Select(i => new IdentityDecl(i.Name, Reference(i.Type), "1",
                i.Components.Select(c => new KeyComp(c.Name, c.Type, c.Normalize, At)).ToList(), null, At)));
            declarations.AddRange((m.Equivalences ?? []).Select(q => new EquivalenceDecl(q.Name, Reference(q.Left), Reference(q.Right), q.Mode ?? "assert", q.Threshold ?? 1m, null, At)));
            declarations.AddRange((m.Rows ?? []).Select(r => new RowsDecl(r.Name,
                r.Fields.Select(f => new FieldDecl(f.Name, f.Optional, ValueType(f.Type), null, At)).ToList(), null, At)));
            declarations.AddRange((m.Constraints ?? []).Select(c => new ConstraintDecl(null, c.Kind, Reference(c.Type), null,
                [], null, null, null, c.Side, c.Min, c.Max ?? -1, [], false, null, At)));
            if (m.Space is { } s)
                declarations.Add(new SpaceDecl(s.Name,
                    s.Sources.Select(x => new SpaceSource(x.Name, x.Adapter, Reference(x.Rows), At)).ToList(),
                    s.Graphs.Select(x => new SpaceGraph(x.Name, x.Master, Reference(x.Projection),
                        x.Inputs.Select(Binding).ToList(), At)).ToList(),
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

    /// <summary>The module text with the digests of its imports replaced by the pins; only the digest values change, the layout stays.</summary>
    public string? RebindPins(SourceFile file, IReadOnlyDictionary<string, string> pins)
    {
        if (!IsModuleFile(file.Path)) return null;
        var root = JsonNode.Parse(file.Text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        var text = file.Text;
        foreach (var import in root["imports"]?.AsArray() ?? [])
            if (import?["module"]?.GetValue<string>() is { } module && pins.TryGetValue(module, out var digest))
                text = System.Text.RegularExpressions.Regex.Replace(text,
                    $@"(""module""\s*:\s*""{System.Text.RegularExpressions.Regex.Escape(module)}""[^{{}}]*?""digest""\s*:\s*)(?:null|""[^""]*"")",
                    m => m.Groups[1].Value + JsonSerializer.Serialize(digest));
        if (JsonNode.Parse(text)!["imports"]?.AsArray().All(i => i?["module"]?.GetValue<string>() is not { } m || !pins.TryGetValue(m, out var d) || i["digest"]?.GetValue<string>() == d) != false) return text;
        // an import without a digest member: write the document again
        foreach (var import in root["imports"]!.AsArray())
            if (pins.TryGetValue(import!["module"]!.GetValue<string>(), out var digest)) import["digest"] = digest;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    // A string connects a source or graph. A literal supplies a typed projection parameter,
    // including Text values that could otherwise be mistaken for names.
    static (string Port, string From, Value? Literal) Binding(KeyValuePair<string, JsonElement> input)
    {
        if (input.Value.ValueKind == JsonValueKind.String) return (input.Key, input.Value.GetString()!, null);
        if (input.Value.ValueKind == JsonValueKind.Object && input.Value.EnumerateObject().ToList() is [{ Name: "lit", Value: var literal }])
            return (input.Key, "", Lowering.Literal(literal));
        throw new JsonException($"binding {input.Key} names a graph or source, or supplies a literal with {{\"lit\": value}}");
    }

    static TypeRef Reference(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? new TypeRef(null, name, At) : new TypeRef(name[..dot], name[(dot + 1)..], At);
    }

    static VType ValueType(string kind) => new(kind, null, null, At);

    static List<Member> Members(List<PropertyDocument>? properties) => (properties ?? []).Select(p =>
        new Member(p.Name, null, p.Optional, ValueType(p.Type), false, p.Unit,
            p.Telemetry is { } t ? new TeleMod(t.Temporality, t.Combine, Norm.DurationNs(t.Period)) : null, null, At,
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
        List<ConstraintDocument>? Constraints, SpaceDocument? Space, List<EquivalenceDocument>? Equivalences = null);
    sealed record ImportDocument(string Module, string Alias, string Version, string? Digest);
    sealed record EntityDocument(string Name, List<PropertyDocument>? Properties);
    sealed record PropertyDocument(string Name, string Type, bool Optional, bool Label, TelemetryDocument? Telemetry,
        string? TreeRole, string? TreeCount, Dictionary<string, string>? Roles, string? Unit = null);
    sealed record TelemetryDocument(string Temporality, string Combine, string Period);
    /// <summary>`Super`: the relationship type this one is a sub-type of, so a pattern or a traverse over the super-type also follows it.</summary>
    sealed record RelationshipDocument(string Name, string From, string To, string? Kind, bool Hierarchy, bool Rollup, List<PropertyDocument>? Properties, string? Super = null);
    /// <summary>Two identity domains whose keys may name the same element, when a link says so: `assert` merges on a link, `review` waits for a decision.</summary>
    sealed record EquivalenceDocument(string Name, string Left, string Right, string? Mode = null, decimal? Threshold = null);
    sealed record IdentityDocument(string Name, string Type, List<ComponentDocument> Components);
    sealed record ComponentDocument(string Name, string Type, string? Normalize);
    sealed record RowsDocument(string Name, List<FieldDocument> Fields);
    sealed record FieldDocument(string Name, string Type, bool Optional);
    sealed record ConstraintDocument(string Kind, string Type, string? Side, long Min, long? Max);
    sealed record SpaceDocument(string Name, List<SourceDocument> Sources, List<GraphDocument> Graphs);
    sealed record SourceDocument(string Name, string Adapter, string Rows);
    sealed record GraphDocument(string Name, string Projection, bool Master, Dictionary<string, JsonElement> Inputs);
}

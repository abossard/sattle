using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>The wire shapes of the canvas GraphSource seam (canvas/README.md): snapshot {descriptor, extent}, GraphPatch, OntologyDefinition, telemetry batches, graph space. Integer revisions are written as decimal strings (Rule 1.2).</summary>
public sealed class CanvasExport(Engine engine, Catalog catalog, bool plain = false)
{
    string Tenant => engine is null ? "lab" : engine.Tenant;
    public bool Plain => plain;
    readonly Dictionary<string, string> typeNames = [], propNames = [];
    readonly Dictionary<string, List<Patch>> patches = [];
    readonly Dictionary<string, List<(Patch? P, IReadOnlyList<WindowChange> T, long Lambda)>> events = [];

    /// <summary>Subscribe before the first ingest to record patch and telemetry history per graph.</summary>
    public void Attach() => engine.Committed += e =>
    {
        if (!events.TryGetValue(e.Graph, out var l)) events[e.Graph] = l = [];
        l.Add((e.Patch, e.Telemetry, e.Lambda));
        if (e.Patch is not null) { if (!patches.TryGetValue(e.Graph, out var p)) patches[e.Graph] = p = []; p.Add(e.Patch); }
    };

    public IReadOnlyList<Patch> PatchesOf(string graph) => patches.GetValueOrDefault(graph) ?? [];

    // ---- names ----
    string TypeName(string id)
    {
        if (typeNames.TryGetValue(id, out var n)) return n;
        var name = new DefView(catalog.Graph).Text(id, "name") ?? Ids.Name(id);
        var clash = !plain && catalog.Graph.Entities.Count(e => Ids.Name(e.Id) == Ids.Name(id) && e.Id != id && (e.Type.EndsWith("EntityType") || e.Type.EndsWith("RelationshipType"))) > 0;
        return typeNames[id] = clash ? Ids.ModuleOf(id) + "." + name : name;
    }
    public string PropNameOf(string id) => PropName(id);
    string PropName(string id)
    {
        if (propNames.TryGetValue(id, out var n)) return n;
        var name = new DefView(catalog.Graph).Text(id, "name") ?? Ids.Name(id);
        var clash = !plain && catalog.Graph.Entities.Count(e => e.Type.EndsWith("PropertyType") && e.Id != id && new DefView(catalog.Graph).Text(e.Id, "name") == name && Ids.ModuleOf(e.Id) != Ids.ModuleOf(id)) > 0;
        return propNames[id] = clash ? Ids.ModuleOf(id) + "." + name : name;
    }

    // ---- values ----
    static JsonNode Id(string v) => new JsonObject { ["value"] = v };
    JsonNode Val(Value v) => v switch
    {
        TextValue t => new JsonObject { ["kind"] = "text", ["value"] = t.S },
        IntegerValue i => new JsonObject { ["kind"] = "integer", ["value"] = i.N.ToString(CultureInfo.InvariantCulture) },
        DecimalValue d => new JsonObject { ["kind"] = "decimal", ["value"] = Norm.Decimal(d.Q) },
        BooleanValue b => new JsonObject { ["kind"] = "boolean", ["value"] = b.B },
        InstantValue n => new JsonObject { ["kind"] = "instant", ["value"] = Norm.Instant(n.Ns) },
        EntityValue e => new JsonObject { ["kind"] = "entity", ["value"] = Id(e.Id) },
        ListValue l => new JsonObject { ["kind"] = "list", ["items"] = new JsonArray([.. l.Items.Select(Val)]) },
        _ => new JsonObject { ["kind"] = "null" },
    };
    JsonNode Props(Props p) => new JsonArray([.. p.Items.Select(kv => (JsonNode)new JsonObject { ["property"] = Id(PropName(kv.Key)), ["value"] = Val(kv.Value) })]);
    JsonNode? State(Element? e) => e switch
    {
        Entity en => new JsonObject { ["id"] = Id(en.Id), ["type"] = Id(TypeName(en.Type)), ["properties"] = Props(en.Props) },
        Rel r => new JsonObject { ["id"] = Id(r.Id), ["source"] = Id(r.Source), ["target"] = Id(r.Target), ["type"] = Id(TypeName(r.Type)), ["properties"] = Props(r.Props) },
        _ => null,
    };

    JsonNode Version(string graph, long revision) => new JsonObject { ["tenant"] = Id(Tenant), ["graph"] = Id(graph), ["revision"] = Id(revision.ToString(CultureInfo.InvariantCulture)) };
    string OutputModule(GraphRuntime g) => g.Typed.OutputModule;

    JsonNode OntologyRef(GraphRuntime g)
    {
        var m = catalog.Modules[OutputModule(g)];
        return new JsonObject { ["id"] = OutputModule(g), ["version"] = m.Version, ["digest"] = Id(m.Digest) };
    }

    public JsonNode Descriptor(GraphRuntime g, long revision) =>
        new JsonObject { ["version"] = Version(g.Name, revision), ["projection"] = Id(g.Typed.Name), ["ontology"] = OntologyRef(g), ["virtualRegions"] = new JsonArray() };

    public JsonNode Snapshot(GraphRuntime g) => SnapshotOf(g.State, Descriptor(g, g.Head), g);

    // ---- references: endpoints of relationships that are not entities of the graph ----

    /// <summary>One endpoint a relationship of a graph names and the graph does not hold. State `inOtherGraph` (the entity is in `Graph`, first the master, then the others in order: draw it or resolve it there) or `absent` (no graph holds it: the source never listed the element, or its row left). Identity: the key the id was minted from (the domain, the identified entity type, the components), so a viewer can label a stub. Uses: how many relationships of which type name it, at which end.</summary>
    public sealed record Reference(string Id, string State, string? Graph, string? EntityType, string? Domain, IReadOnlyList<(string Name, string Value)> Key, IReadOnlyList<(string RelType, string End, int N)> Uses)
    {
        public JsonNode ToJson(Func<string?, string?> typeName) => new JsonObject
        {
            ["id"] = new JsonObject { ["value"] = Id }, ["state"] = State, ["graph"] = Graph, ["entityType"] = typeName(EntityType),
            ["identity"] = Domain is null ? null : new JsonObject { ["domain"] = Domain, ["key"] = new JsonObject([.. Key.Select(k => KeyValuePair.Create(k.Name, (JsonNode?)k.Value))]) },
            ["uses"] = new JsonArray([.. Uses.Select(u => (JsonNode)new JsonObject { ["relationshipType"] = u.RelType, ["end"] = u.End, ["count"] = u.N })]),
        };
    }

    public string TypeLabel(string id) => TypeName(id);
    public string? TypeLabelOrNull(string? id) => TypeNameOrNull(id);
    string? TypeNameOrNull(string? id) => id is null ? null : TypeName(id);

    (long Version, Dictionary<string, (IdDomain Domain, string[] Components)> Map) identityIndex = (-1, []);
    Dictionary<string, (IdDomain Domain, string[] Components)> IdentityIndex()
    {
        var version = (long)engine.Identities.Count * 1_000_003 + engine.Identities.Forwarding.Count;
        if (identityIndex.Version == version) return identityIndex.Map;
        var map = new Dictionary<string, (IdDomain, string[])>(StringComparer.Ordinal);
        foreach (var (d, id, comps) in engine.Identities.Bindings()) if (!map.TryGetValue(id, out var old) || (old.Item1.IdentifiesType is null && d.IdentifiesType is not null)) map[id] = (d, comps);
        identityIndex = (version, map);
        return map;
    }

    /// <summary>The endpoints of the graph's relationships that are not entities of the graph, each once, in id order.</summary>
    public IReadOnlyList<Reference> Unresolved(GraphRuntime g) => Unresolved(g.State, g);

    public IReadOnlyList<Reference> Unresolved(Graph state, GraphRuntime g)
    {
        var missing = new Dictionary<string, Dictionary<(string, string), int>>(StringComparer.Ordinal);
        foreach (var r in state.Rels)
            foreach (var (id, end) in new[] { (r.Source, "source"), (r.Target, "target") })
                if (state.Entity(id) is null)
                {
                    if (!missing.TryGetValue(id, out var uses)) missing[id] = uses = [];
                    var k = (TypeName(r.Type), end); uses[k] = uses.GetValueOrDefault(k) + 1;
                }
        if (missing.Count == 0) return [];
        var index = IdentityIndex();
        var others = engine.Graphs.Where(o => o != g).OrderBy(o => o.IsMaster ? 0 : 1).ToList();
        return [.. missing.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
        {
            var where = others.FirstOrDefault(o => o.State.Entity(x.Key) is not null);
            index.TryGetValue(x.Key, out var ident);
            var key = ident.Domain is null ? [] : ident.Domain.CompNames.Zip(ident.Components, (n, v) => (n, v)).ToList();
            return new Reference(x.Key, where is null ? "absent" : "inOtherGraph", where?.Name, ident.Domain?.IdentifiesType, ident.Domain?.Id, key,
                [.. x.Value.OrderBy(u => u.Key).Select(u => (u.Key.Item1, u.Key.Item2, u.Value))]);
        })];
    }

    /// <summary>The snapshot at revision 0 and the patches 1..head reproduce the final graph: the canvas replays them in order.</summary>
    public JsonNode Genesis(GraphRuntime g) =>
        new JsonObject { ["descriptor"] = Descriptor(g, 0), ["extent"] = new JsonObject { ["entities"] = new JsonArray(), ["relationships"] = new JsonArray() } };

    public JsonNode Patch(Patch p, string tenant = "") => new JsonObject
    {
        ["schemaVersion"] = "1.0", ["from"] = Version(p.Graph, p.From), ["to"] = Version(p.Graph, p.To), ["projection"] = Id(engine is null ? p.Graph : engine.Graph(p.Graph).Typed.Name), ["profile"] = "extensionalFinite",
        ["edits"] = new JsonArray([.. p.Edits.Where(e => e.Key.Kind != 'o').Select(e => (JsonNode)new JsonObject
        {
            ["kind"] = e.Key.Kind == 'e' ? "entity" : "relationship", ["id"] = Id(e.Key.Id), ["before"] = State(e.Before), ["after"] = State(e.After),
        })]),
        ["cause"] = new JsonObject { ["inputs"] = new JsonArray([.. p.Cause.Inputs.Select(i => Id(i))]), ["configurationVersion"] = p.Cause.Configuration },
    };

    public JsonNode Ontology(GraphRuntime g) => OntologyOfModule(OutputModule(g), OntologyRef(g), excludeKernel: true);

    /// <summary>The kernel as the ontology of the catalog graph.</summary>
    public JsonNode KernelOntology() => OntologyOfModule(Ids.KernelModule, new JsonObject { ["id"] = Ids.KernelModule, ["version"] = Kernel.Version, ["digest"] = Id(Kernel.CompiledDigest) }, excludeKernel: false);

    public JsonNode CatalogDescriptor(long revision) =>
        new JsonObject { ["version"] = Version("catalog", revision), ["projection"] = Id("catalog"), ["ontology"] = new JsonObject { ["id"] = Ids.KernelModule, ["version"] = Kernel.Version, ["digest"] = Id(Kernel.CompiledDigest) }, ["virtualRegions"] = new JsonArray() };

    /// <summary>A snapshot of `g`. With `self` (the graph of the space it is a state of) the extent also lists the references: the endpoints its relationships name that it does not hold, where the other graphs are read at their head.</summary>
    public JsonNode SnapshotOf(Graph g, JsonNode descriptor, GraphRuntime? self = null)
    {
        var ents = g.Entities.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => State(e)!);
        var rels = g.Rels.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => State(e)!);
        var extent = new JsonObject { ["entities"] = new JsonArray([.. ents]), ["relationships"] = new JsonArray([.. rels]) };
        if (self is not null) extent["references"] = new JsonArray([.. Unresolved(g, self).Select(u => u.ToJson(TypeNameOrNull))]);
        return new JsonObject { ["descriptor"] = descriptor, ["extent"] = extent };
    }

    /// <summary>For an entity type whose properties carry a `treerole`: which property is the own state, the rolled-up state, the severity, the kind and the count of each state, by the names the snapshots key them with. Null when the type has none.</summary>
    JsonNode? TreeState(OntologyView view, string typeId)
    {
        var dv = new DefView(catalog.Graph);
        var roles = view.PropsOf(typeId).Keys.Where(view.Props.ContainsKey).Select(p => (Prop: p, Role: dv.Text(p, "treeRole"))).Where(x => x.Role is not null).ToList();
        if (roles.Count == 0) return null;
        var o = new JsonObject(); var counts = new JsonObject();
        foreach (var (p, r) in roles.OrderBy(x => x.Prop, StringComparer.Ordinal))
            switch (r) { case "own" or "rolledUp" or "severity" or "kind" or "curatedBy" or "critical" or "hidden" or "mode" or "share": o[r] = PropName(p); break; case "healthy" or "degraded" or "unhealthy" or "unknown": counts[char.ToUpperInvariant(r![0]) + r[1..]] = PropName(p); break; }
        if (counts.Count > 0) o["counts"] = counts;
        return o;
    }

    JsonNode OntologyOfModule(string module, JsonNode reference, bool excludeKernel)
    {
        var view = catalog.OntologyOf(module);
        var types = view.Types.Values.Where(t => !excludeKernel || Ids.ModuleOf(t.Id) != Ids.KernelModule).ToList();
        var propIds = types.SelectMany(t => view.PropsOf(t.Id).Keys).Distinct().Where(view.Props.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
        JsonNode PropDef(string id)
        {
            var p = view.Props[id];
            var o = new JsonObject
            {
                ["id"] = Id(PropName(id)), ["valueKind"] = p.ValueKind == "Any" ? "text" : p.ValueKind.ToLowerInvariant(), ["nullable"] = p.Nullable, ["itemKind"] = p.ItemKind?.ToLowerInvariant(), ["unit"] = p.Unit, ["role"] = p.Role,
            };
            if (p.Presentation is { Count: > 0 } pr) o["presentation"] = new JsonArray([.. pr.Select(x => (JsonNode)new JsonObject { ["value"] = Val(x.Value), ["role"] = x.Role })]);
            return o;
        }
        JsonArray Names(IEnumerable<string> props) => new([.. props.Where(view.Props.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).Select(p => (JsonNode)Id(PropName(p)))]);
        return new JsonObject
        {
            ["schemaVersion"] = "1.0", ["reference"] = reference, ["noEntityType"] = Id("NoType"), ["noRelationshipType"] = Id("NoType"),
            ["properties"] = new JsonArray([.. propIds.Select(PropDef)]),
            ["entityTypes"] = new JsonArray([.. new[] { (JsonNode)new JsonObject { ["id"] = Id("NoType"), ["properties"] = new JsonArray() } }.Concat(types.Where(t => !t.IsRel).OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject { ["id"] = Id(TypeName(t.Id)), ["properties"] = Names(view.PropsOf(t.Id).Keys), ["treeState"] = TreeState(view, t.Id) }))]),
            ["relationshipTypes"] = new JsonArray([.. new[] { (JsonNode)new JsonObject { ["id"] = Id("NoType"), ["sourceTypes"] = new JsonArray(), ["targetTypes"] = new JsonArray(), ["properties"] = new JsonArray() } }.Concat(types.Where(t => t.IsRel).OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
            {
                ["id"] = Id(TypeName(t.Id)), ["sourceTypes"] = new JsonArray([.. t.Sources.OrderBy(x => x, StringComparer.Ordinal).Select(s => (JsonNode)Id(TypeName(s)))]), ["targetTypes"] = new JsonArray([.. t.Targets.OrderBy(x => x, StringComparer.Ordinal).Select(s => (JsonNode)Id(TypeName(s)))]), ["properties"] = Names(view.PropsOf(t.Id).Keys),
                ["hierarchy"] = new DefView(catalog.Graph).Bool(t.Id, "hierarchy"),
            }))]),
        };
    }

    /// <summary>Telemetry as sparse columnar batches, one per (window, property, target). Ticks are window ordinals: the engine's telemetry is indexed by window start, not by revision (a seam mismatch, see README).</summary>
    public IEnumerable<JsonNode> TelemetryBatches(GraphRuntime g)
    {
        if (engine.Telemetry is not InMemoryTelemetryStore mem) yield break;
        var dump = mem.Dump(g.Key);
        var windows = dump.Select(x => x.Ws).Distinct().Order().ToList();
        if (windows.Count == 0) yield break;
        var tickOf = windows.Select((w, i) => (w, i)).ToDictionary(x => x.w, x => x.i + 1);
        foreach (var grp in dump.GroupBy(x => (x.Ws, x.Prop)).OrderBy(x => x.Key.Ws).ThenBy(x => engine.Interner.Get(x.Key.Prop), StringComparer.Ordinal))
        {
            var rows = grp.Where(x => g.State.Get(ElementKey.Entity(engine.Interner.Get(x.Element))) is not null || g.State.Get(ElementKey.Rel(engine.Interner.Get(x.Element))) is not null).ToList();
            if (rows.Count == 0) continue;
            var isEdge = engine.Interner.Get(rows[0].Element).StartsWith("r:", StringComparison.Ordinal);
            yield return new JsonObject
            {
                ["graph"] = g.Name, ["tick"] = tickOf[grp.Key.Ws], ["t"] = (grp.Key.Ws - windows[0]) / 1e9, ["target"] = isEdge ? "edge" : "node", ["property"] = PropName(engine.Interner.Get(grp.Key.Prop)),
                ["ids"] = new JsonArray([.. rows.Select(r => (JsonNode)engine.Interner.Get(r.Element))]), ["values"] = new JsonArray([.. rows.Select(r => (JsonNode)(double)r.Value)]),
            };
        }
    }

    public JsonNode Meters(GraphRuntime g)
    {
        var view = catalog.OntologyOf(OutputModule(g));
        var tele = view.Props.Values.Where(p => p.Role == "telemetry" && Ids.ModuleOf(p.Id) != Ids.KernelModule).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var edgeProps = view.Types.Values.Where(t => t.IsRel).SelectMany(t => t.OwnProps.Keys).ToHashSet();
        var dump = engine.Telemetry is InMemoryTelemetryStore mem ? mem.Dump(g.Key) : [];
        return new JsonArray([.. tele.Select(p =>
        {
            var vals = dump.Where(x => engine.Interner.Get(x.Prop) == p.Id).Select(x => (double)x.Value).ToList();
            var max = vals.Count == 0 ? 1 : Math.Max(vals.Max(), 1e-9);
            return (JsonNode)new JsonObject { ["id"] = PropName(p.Id), ["target"] = edgeProps.Contains(p.Id) ? "edge" : "node", ["label"] = p.Name, ["unit"] = p.Unit ?? "", ["min"] = 0, ["max"] = Math.Round(max, 6), ["scale"] = "sqrt" };
        })]);
    }

    /// <summary>GraphSpaceDefinition with stub projection definitions: the canvas ProjectionDefinition has no counterpart for definition graphs (see README).</summary>
    public JsonNode SpaceDefinition(TypedSpace space)
    {
        JsonNode Projection(GraphRuntime g) => new JsonObject
        {
            ["schemaVersion"] = "1.0", ["id"] = Id(g.Typed.Name), ["version"] = catalog.Modules[g.Typed.Module].Version, ["ontology"] = OntologyRef(g),
            ["inputs"] = new JsonArray([.. g.Typed.Ports.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["contract"] = p.Kind == "rows" ? Ids.Name(p.RowTypeId!) : "sattle.graph", ["version"] = "1" })]),
            ["scaffold"] = new JsonObject { ["entities"] = new JsonArray(), ["relationships"] = new JsonArray() },
            ["operators"] = new JsonArray(), ["wires"] = new JsonArray(), ["outputs"] = new JsonArray(), ["retainedInputsPerElement"] = g.Typed.Retain, ["bidirectionalRules"] = new JsonArray(),
        };
        var master = engine.Master;
        var derived = engine.Graphs.Where(g => !g.IsMaster).Select(g =>
        {
            var tg = space.Graphs.First(x => x.Name == g.Name);
            return (JsonNode)new JsonObject
            {
                ["outputGraph"] = Id(g.Name), ["projection"] = Projection(g),
                ["graphInputs"] = new JsonArray([.. tg.Bindings.Where(kv => engine.Graphs.Any(x => x.Name == kv.Value)).Select(kv =>
                {
                    var up = engine.Graph(kv.Value);
                    return (JsonNode)new JsonObject
                    {
                        ["port"] = kv.Key, ["source"] = up.IsMaster ? new JsonObject { ["kind"] = "master" } : new JsonObject { ["kind"] = "projection", ["projection"] = Id(up.Typed.Name) },
                        ["expectedOntology"] = OntologyRef(up),
                    };
                })]),
            };
        });
        return new JsonObject { ["schemaVersion"] = "1.0", ["id"] = space.Name, ["master"] = new JsonObject { ["graph"] = Id(master.Name), ["ingestionProjection"] = Projection(master) }, ["projections"] = new JsonArray([.. derived]) };
    }

    /// <summary>The meter declarations of a graph: every telemetry property of its ontology with unit, temporality, combine, period, the largest value seen (for scaling) and the warn line of a threshold on it, and the thresholds themselves. `GET /graphs/{g}/meters` and `NAME.meters.json` of `replay --out`.</summary>
    public JsonNode MetersDocument(GraphRuntime rt)
    {
        var g = rt.Name;
            var view = catalog.OntologyOf(rt.Typed.OutputModule);
            var mem = (InMemoryTelemetryStore)engine.Telemetry;
            var dump = mem.Dump(rt.Key);
            var edgeProps = view.Types.Values.Where(t => t.IsRel).SelectMany(t => t.OwnProps.Keys).ToHashSet();
            var thresholds = rt.Typed.Ops.OfType<TThreshold>().Select(t => (Op: t, Meter: t.Value is TField f ? f.Name : null)).ToList();
            var tele = view.Props.Values.Where(p => p.Role == "telemetry" && Ids.ModuleOf(p.Id) == rt.Typed.OutputModule || p.Role == "telemetry" && Ids.ModuleOf(p.Id) != Ids.KernelModule && rt.Typed.Ops.OfType<TEmitEntity>().Any(e => e.Sets.Any(s => s.PropId == p.Id)) ).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            var meters = new JsonArray([.. tele.Select(p =>
            {
                var vals = dump.Where(x => engine.Interner.Get(x.Prop) == p.Id).Select(x => (double)x.Value).ToList();
                var warn = thresholds.Where(t => t.Meter == p.Name && t.Op.AboveBy is null).Select(t => (double?)(double)t.Op.Above).FirstOrDefault();
                return (JsonNode)new JsonObject
                {
                    ["id"] = PropNameOf(p.Id), ["target"] = edgeProps.Contains(p.Id) ? "edge" : "node", ["label"] = p.Name, ["unit"] = p.Unit ?? "", ["temporality"] = p.Temporality, ["combine"] = p.Combine,
                    ["periodSeconds"] = p.Period / 1_000_000_000, ["min"] = 0, ["max"] = vals.Count == 0 ? 1 : Math.Round(vals.Max(), 6), ["scale"] = "sqrt", ["warn"] = warn,
                };
            })]);
            // a parameter that is read from the row (`above above for enterAfter`) has no one value: it is null, and `byRow` names the column it is read from
            static string? Column(TExpr? by) => by is null ? null : by is TField f ? f.Name : "expression";
            var ths = new JsonArray([.. thresholds.Select(t =>
            {
                var o = new JsonObject
                {
                    ["graph"] = g, ["operator"] = t.Op.Name, ["meter"] = t.Meter,
                    ["above"] = t.Op.AboveBy is null ? (double)t.Op.Above : null, ["enterAfter"] = t.Op.EnterBy is null ? t.Op.EnterAfter : null,
                    ["below"] = t.Op.BelowBy is null ? (double)t.Op.Below : null, ["exitAfter"] = t.Op.ExitBy is null ? t.Op.ExitAfter : null,
                    ["state"] = t.Op.Target, ["high"] = t.Op.High, ["low"] = t.Op.Low,
                };
                var byRow = new JsonObject();
                foreach (var (name, by) in new[] { ("above", t.Op.AboveBy), ("enterAfter", t.Op.EnterBy), ("below", t.Op.BelowBy), ("exitAfter", t.Op.ExitBy) }) if (Column(by) is { } c) byRow[name] = c;
                if (byRow.Count > 0) o["byRow"] = byRow;
                return (JsonNode)o;
            })]);
            return new JsonObject { ["meters"] = meters, ["thresholds"] = ths, ["defaults"] = new JsonObject() };
    }

    /// <summary>Writes the NDJSON and JSON files the canvas StaticSource reads: manifest.json, space.json, per graph ontology, snapshot, patches and telemetry.</summary>
    public void WriteFiles(string dir, TypedSpace space)
    {
        Directory.CreateDirectory(dir);
        static void Write(string path, JsonNode n) => File.WriteAllText(path, n.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var graphsNode = new JsonObject();
        var lineage = new LineageReader(engine);
        foreach (var g in engine.Graphs)
        {
            Write(Path.Combine(dir, $"{g.Name}.ontology.json"), Ontology(g));
            Write(Path.Combine(dir, $"{g.Name}.snapshot.json"), Genesis(g));
            Write(Path.Combine(dir, $"{g.Name}.final.snapshot.json"), Snapshot(g));
            File.WriteAllLines(Path.Combine(dir, $"{g.Name}.patches.ndjson"), PatchesOf(g.Name).Select(p => Patch(p).ToJsonString()));
            File.WriteAllText(Path.Combine(dir, $"{g.Name}.patches.json"), new JsonArray([.. PatchesOf(g.Name).Select(p => Patch(p))]).ToJsonString());
            File.WriteAllLines(Path.Combine(dir, $"{g.Name}.telemetry.ndjson"), TelemetryBatches(g).Select(b => b.ToJsonString()));
            var entry = new JsonObject { ["ontology"] = $"{g.Name}.ontology.json", ["snapshot"] = $"{g.Name}.snapshot.json", ["patches"] = $"{g.Name}.patches.json" };
            // what the live engine serves for the same graph: `GET /lineage` of every element (complete), `GET /lineage/reverse` of every element that is part of something, the rendered document
            Write(Path.Combine(dir, $"{g.Name}.meters.json"), MetersDocument(g)); entry["meters"] = $"{g.Name}.meters.json";
            File.WriteAllLines(Path.Combine(dir, $"{g.Name}.references.ndjson"), Unresolved(g).Select(u => u.ToJson(TypeNameOrNull).ToJsonString())); entry["references"] = $"{g.Name}.references.ndjson";
            File.WriteAllLines(Path.Combine(dir, $"{g.Name}.lineage.ndjson"), lineage.AllForward(g.Name).Select(n => n.ToJsonString()));
            File.WriteAllLines(Path.Combine(dir, $"{g.Name}.lineage.reverse.ndjson"), lineage.AllReverse(g.Name).Select(n => n.ToJsonString()));
            entry["lineage"] = $"{g.Name}.lineage.ndjson"; entry["lineageReverse"] = $"{g.Name}.lineage.reverse.ndjson";
            if (g.Typed.OutputModule == Documents.Module)
            {
                var blocks = Documents.BlocksOf(g.State); var md = Documents.Markdown(blocks); var digest = Documents.Digest(md);
                File.WriteAllText(Path.Combine(dir, $"{g.Name}.document.md"), md);
                Write(Path.Combine(dir, $"{g.Name}.document.json"), Documents.Json(g.Head, blocks, digest));
                entry["document"] = $"{g.Name}.document.md"; entry["documentJson"] = $"{g.Name}.document.json";
            }
            graphsNode[g.Name] = entry;
        }
        Write(Path.Combine(dir, "space.json"), SpaceDefinition(space));
        Write(Path.Combine(dir, "manifest.json"), new JsonObject { ["space"] = "space.json", ["graphs"] = graphsNode });
    }
}

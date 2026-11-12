using System.Globalization;
using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>Backward edits (research 03 section 4.4): a view edit becomes a proposal on the master's authored partition (lens put), then authored rows (bijective inverse), is checked on a staged copy (PutGet), and only then accepted as recorded input rows.</summary>
public sealed partial class SpaceHost
{
    static string TypeLabel(Catalog c, string id) => new DefView(c.Graph).Text(id, "name") ?? Ids.Name(id);

    IEnumerable<string> Owned(TypedProjection tp, string rule)
    {
        var o = Catalog.OntologyOf(tp.OutputModule);
        foreach (var e in tp.Ops.OfType<TEmitEntity>().Where(e => e.Rule == rule))
        {
            yield return TypeLabel(Catalog, e.TypeId);
            foreach (var s in e.Sets) yield return TypeLabel(Catalog, e.TypeId) + "." + s.PropName;
        }
        foreach (var e in tp.Ops.OfType<TEmitRel>().Where(e => e.Rule == rule)) yield return TypeLabel(Catalog, e.TypeId);
        _ = o;
    }

    sealed record ViewEdit(char Kind, string? Id, Element? Before, Element? After, JsonNode? Key);

    HostError Reject(string code, string message, string graph, string? element = null, string? rule = null, int status = 422) =>
        new(code, message, status, new JsonObject { ["graph"] = graph, ["element"] = element, ["rule"] = rule });

    /// <summary>The inverse of a bijective ingestion rule over an authored rows port: row field per entity property, key field, and the parent field carried by a relationship.</summary>
    sealed record RowInverse(string Source, string RelTypeId, string EntityTypeId, string KeyField, string ParentField, Dictionary<string, string> FieldProp, string Rule);

    RowInverse? InverseOf(TypedProjection master)
    {
        foreach (var r in master.Rules.Where(r => r.Class == "bijective"))
        {
            var ent = master.Ops.OfType<TEmitEntity>().FirstOrDefault(e => e.Rule == r.Name && !e.Update);
            var rel = master.Ops.OfType<TEmitRel>().FirstOrDefault(e => e.Rule == r.Name && !e.Update && e.Source is TId && e.Target is TId);
            if (ent is null || rel is null || !ent.Inputs.TryGetValue("in", out var src) || !src.IsPort) continue;
            var fieldProp = new Dictionary<string, string>();
            foreach (var s in ent.Sets) if (s.Value is TField f) fieldProp[f.Name] = s.PropId;
            var key = ((TId)rel.Source).Comps.Select(c => c.Value).OfType<TField>().FirstOrDefault()?.Name;
            var parent = ((TId)rel.Target).Comps.Select(c => c.Value).OfType<TField>().FirstOrDefault()?.Name;
            if (key is null || parent is null) continue;
            var sourceName = Space.Graphs.First(g => g.Master).Bindings[src.Name];
            return new RowInverse(sourceName, rel.TypeId, ent.TypeId, key, parent, fieldProp, r.Name);
        }
        return null;
    }

    sealed record CapRow(string Key, Dictionary<string, string?> Fields);

    /// <summary>The authored table of the master: one row per entity of the rule's type, with the parent from its outgoing relationship.</summary>
    Dictionary<string, Dictionary<string, string?>> Table(Graph master, RowInverse inv)
    {
        var rows = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var e in master.Entities.Where(e => e.Type == inv.EntityTypeId))
        {
            var f = new Dictionary<string, string?>();
            foreach (var (field, prop) in inv.FieldProp) f[field] = e.Props.Text(prop);
            f[inv.ParentField] = null;
            foreach (var rid in master.OutRels(e.Id).Select(master.Rel).Where(r => r is not null && r.Type == inv.RelTypeId))
                if (master.Entity(rid!.Target) is { } p) f[inv.ParentField] = p.Props.Text(inv.FieldProp[inv.KeyField]);
            if (f[inv.KeyField] is { } k) rows[k] = f;
        }
        return rows;
    }

    Element? Decode(JsonNode? n, Catalog cat, string graph)
    {
        if (n is null) return null;
        var view = cat.OntologyOf(graph == "master" ? Engine.Master.Typed.OutputModule : Engine.Graph(graph).Typed.OutputModule);
        string TypeId(string name) => view.Types.Values.FirstOrDefault(t => TypeLabel(cat, t.Id) == name)?.Id ?? throw Reject("BadEdit", $"unknown type '{name}'", graph);
        string PropId(string type, string name) => view.PropsOf(type).Keys.FirstOrDefault(k => view.Props.TryGetValue(k, out var p) && (export.PropNameOf(k) == name || p.Name == name)) ?? throw Reject("BadEdit", $"{TypeLabel(cat, type)} has no property '{name}'", graph);
        Props Props(string type, JsonNode? arr) => Core.Props.Of((arr?.AsArray() ?? []).Select(x => KeyValuePair.Create(PropId(type, x!["property"]!["value"]!.GetValue<string>()), DecodeValue(x["value"]!))));
        var id = n["id"]!["value"]!.GetValue<string>(); var type = TypeId(n["type"]!["value"]!.GetValue<string>());
        return n["source"] is null ? new Entity(id, type, Props(type, n["properties"])) : new Rel(id, n["source"]!["value"]!.GetValue<string>(), n["target"]!["value"]!.GetValue<string>(), type, Props(type, n["properties"]));
    }

    internal static Value DecodeValue(JsonNode v) => v["kind"]!.GetValue<string>() switch
    {
        "text" => Value.Text(v["value"]!.GetValue<string>()),
        "integer" => Value.Integer(System.Numerics.BigInteger.Parse(v["value"]!.GetValue<string>(), CultureInfo.InvariantCulture)),
        "decimal" => Value.Decimal(decimal.Parse(v["value"]!.GetValue<string>(), CultureInfo.InvariantCulture)),
        "boolean" => Value.Boolean(v["value"]!.GetValue<bool>()),
        "instant" => Norm.TryParseInstant(v["value"]!.GetValue<string>(), out var ns) ? Value.Instant(ns) : throw new HostError("BadEdit", "bad instant", 422),
        "entity" => Value.Entity(v["value"]!["value"]!.GetValue<string>()),
        "list" => Value.List((v["items"]?.AsArray() ?? []).Select(x => DecodeValue(x!))),
        _ => Value.Null,
    };

    public JsonNode ApplyEdits(string g, JsonNode body)
    {
        lock (Gate)
        {
            if (g == "catalog") throw Reject("NotEditable", "the catalog is edited through POST /definitions/{module}", g);
            var rt = Engine.Graph(g);
            var tp = rt.Typed;
            if (tp.Rules.Count == 0 || tp.Forward) throw Reject("NotEditable", $"graph {g} is produced by a forward projection", g);
            var master = Engine.Master;
            var inv = InverseOf(master.Typed) ?? throw Reject("NotEditable", "the master graph has no bijective ingestion rule to carry edits", g);
            // bases for every graph on the path
            var bases = new List<string> { g }; if (!rt.IsMaster) bases.Add(master.Name);
            var given = body["baseRevisions"]?.AsObject() ?? [];
            foreach (var b in bases)
            {
                if (given[b] is null) throw Reject("BadEdit", $"baseRevisions must name {b}", g);
                var actual = served[b].Head;
                if (given[b]!.GetValue<string>() != Rev(actual)) throw new HostError("StaleBase", $"graph {b} is at revision {actual}", 409, new JsonObject { ["graph"] = b, ["actual"] = Rev(actual) });
            }
            var edits = new List<ViewEdit>();
            foreach (var n in (body["edits"]?.AsArray() ?? []))
                edits.Add(new ViewEdit(n!["kind"]!.GetValue<string>() == "entity" ? 'e' : 'r', n["id"]?.GetValue<string>(), Decode(n["before"], Catalog, g), Decode(n["after"], Catalog, g), n["key"]));
            edits.RemoveAll(e => e.Before is not null && Equals(e.Before, e.After));   // GetPut: putting back an unchanged element changes nothing
            if (edits.Count == 0) return new JsonObject { ["accepted"] = true, ["revisions"] = new JsonObject { [g] = Rev(served[g].Head) } };

            // 1. ownership and acceptance per edit
            var proposal = master.State.Clone();
            var overlay = new List<Edit>();   // edits on the master's authored partition
            if (rt.IsMaster) Classify(tp, g, edits, overlay, inv, rt);
            else Backward(tp, g, rt, edits, overlay, inv);
            if (overlay.Count > 0)
            {
                var err = proposal.Apply(overlay);
                if (err is not null) throw Reject("BadEdit", err, g);
            }

            // 2. bijective inverse: authored rows
            var before = Table(master.State, inv); var after = Table(proposal, inv);
            var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var old) || !old.SequenceEqual(kv.Value)).ToList();
            var removed = before.Where(kv => !after.ContainsKey(kv.Key)).ToList();
            if (removed.Count > 0) throw Reject("BadEdit", "authored rows cannot be removed, only retired", g);

            // 3. staged forward evaluation: authored rows only, on a fresh engine
            using var staged = new Engine(Catalog, spaceId, new EngineOptions { Tenant = options.Tenant, Only = new HashSet<string> { g } }, interner);
            StageRows(staged, inv, after, 1);
            var expected = (rt.IsMaster ? master.State : rt.State).Clone();
            var apply = expected.Apply([.. edits.Select(e => new Edit(e.Kind == 'e' ? ElementKey.Entity(e.Id ?? e.After!.Key.Id) : ElementKey.Rel(e.Id ?? e.After!.Key.Id), e.Before, e.After))]);
            if (apply is not null) throw Reject("BadEdit", apply, g);
            var actualView = staged.Graph(g).State;
            if (OwnedDiff(tp, g, expected, actualView, rt.IsMaster) is { } diff) throw Reject("PutGetViolation", $"forward evaluation of the proposal does not reproduce the edited view: {diff}", g, diff);
            var cons = Conformance.Check(staged.Master.State, Catalog.OntologyOf(staged.Master.Typed.OutputModule)).Concat(Conformance.Check(actualView, Catalog.OntologyOf(staged.Graph(g).Typed.OutputModule))).FirstOrDefault(d => d.IsError);
            if (cons is not null) throw Reject("ConstraintViolation", cons.Message, g, cons.Focus);

            // 4. accept: the rows become recorded input
            if (changed.Count > 0)
            {
                var w = new RowWriter(RowLayout(inv.Source), interner);
                foreach (var (k, row) in changed)
                {
                    if (before.TryGetValue(k, out var old)) { w.Begin(-1); WriteRow(w, old); }
                    w.Begin(1); WriteRow(w, row);
                }
                Ingest(inv.Source, w.Build()!, 1_000_000 + inputs.Count);
            }
            var rev = new JsonObject();
            foreach (var b in bases) rev[b] = Rev(served[b].Head);
            return new JsonObject { ["accepted"] = true, ["revisions"] = rev };
        }
    }

    void WriteRow(RowWriter w, Dictionary<string, string?> row) { foreach (var (f, v) in row) w.Text(f, v); }

    void StageRows(Engine staged, RowInverse inv, Dictionary<string, Dictionary<string, string?>> table, long seq)
    {
        if (table.Count == 0) return;
        var w = new RowWriter(staged.RowLayout(inv.Source), interner);
        foreach (var row in table.Values.OrderBy(r => r[inv.KeyField], StringComparer.Ordinal)) { w.Begin(); WriteRow(w, row); }
        staged.Ingest(inv.Source, w.Build()!, seq);
    }

    string RuleOf(TypedProjection tp, string typeId, bool rel) =>
        (rel ? tp.Ops.OfType<TEmitRel>().Where(e => !e.Update && e.TypeId == typeId).Select(e => e.Rule) : tp.Ops.OfType<TEmitEntity>().Where(e => !e.Update && e.TypeId == typeId).Select(e => e.Rule)).FirstOrDefault() ?? "";

    TRule RuleNamed(TypedProjection tp, string name) => tp.Rules.FirstOrDefault(r => r.Name == name) ?? new TRule(name, "readonly", null, []);

    /// <summary>Edits addressed to the master graph: elements of the bijective rule's types pass through, everything else is owned by a readonly rule.</summary>
    void Classify(TypedProjection tp, string g, List<ViewEdit> edits, List<Edit> overlay, RowInverse inv, GraphRuntime rt)
    {
        foreach (var e in edits)
        {
            var el = e.After ?? e.Before!;
            var typeId = el is Entity en ? en.Type : ((Rel)el).Type;
            if (typeId != inv.EntityTypeId && typeId != inv.RelTypeId)
            {
                var rule = RuleOf(tp, typeId, el is Rel);
                throw Reject(RuleNamed(tp, rule).Class == "readonly" ? "ReadOnlyByRule" : "NotEditable", $"{TypeLabel(Catalog, typeId)} is owned by rule {rule}", g, el.Key.Id, rule);
            }
            overlay.Add(new Edit(el.Key, e.Before, e.After));
        }
    }

    /// <summary>Lens put: view edits become master edits, by the rule that owns the touched element (P2).</summary>
    void Backward(TypedProjection tp, string g, GraphRuntime rt, List<ViewEdit> edits, List<Edit> overlay, RowInverse inv)
    {
        var master = Engine.Master.State;
        for (var ei = 0; ei < edits.Count; ei++)
        {
            var e = edits[ei];
            var el = e.After ?? e.Before!;
            if (el is Entity en)
            {
                var sink = tp.Ops.OfType<TEmitEntity>().FirstOrDefault(s => !s.Update && s.TypeId == en.Type) ?? throw Reject("NotEditable", $"no rule emits {TypeLabel(Catalog, en.Type)}", g, en.Id);
                var rule = RuleNamed(tp, sink.Rule ?? "");
                // properties set by an update emit of another rule are read-only; telemetry never
                if (e.Before is Entity b0 && e.After is Entity a0)
                    foreach (var (k, v) in a0.Props.Items.Where(kv => !(b0.Props.Get(kv.Key)?.Equals(kv.Value) ?? false)).Concat(b0.Props.Items.Where(kv => a0.Props.Get(kv.Key) is null)))
                    {
                        var owner = tp.Ops.OfType<TEmitEntity>().Where(s => s.TypeId == en.Type && s.Sets.Any(x => x.PropId == k)).Select(s => s.Rule).FirstOrDefault();
                        var pi = Catalog.OntologyOf(tp.OutputModule).Props.GetValueOrDefault(k);
                        if (pi?.Role == "telemetry") throw Reject("TelemetryNotEditable", $"{pi.Name} is telemetry", g, en.Id);
                        if (owner is not null && RuleNamed(tp, owner).Class == "readonly") throw Reject("ReadOnlyByRule", $"property {pi?.Name} is owned by rule {owner}", g, en.Id, owner);
                    }
                var policy = rule.Policy;
                var kind = e.Before is null ? "create" : e.After is null ? "delete" : "update";
                var updated = kind == "update" ? string.Join(",", ChangedNames(tp, sink, (Entity)e.Before!, (Entity)e.After!)) : "";
                if (rule.Class != "lens" || policy is null || !(kind == "update" ? policy.Accept.Any(a => a.StartsWith("update(", StringComparison.Ordinal) && updated.Split(',').All(u => a[7..^1].Split(',').Contains(u))) : policy.Accept.Contains(kind)))
                    throw Reject("NotAccepted", $"rule {rule.Name} does not accept {kind}{(updated.Length > 0 ? $"({updated})" : "")}", g, en.Id, rule.Name);
                // key and id
                // the type the view's element comes from: the one its create key identifies, else the type of the element the emit reads (a lens that accepts no create has no create key)
                var src = policy.SourceTypeId ?? sink.EntityId.Type.Ty.TypeId ?? throw Reject("NotEditable", $"rule {rule.Name} names no source type for {TypeLabel(Catalog, en.Type)}", g, en.Id, rule.Name);
                var view = Catalog.OntologyOf(Engine.Master.Typed.OutputModule);
                if (kind == "create")
                {
                    var key = e.Key ?? throw Reject("KeyMismatch", "a created element needs a key", g, en.Id);
                    var comps = policy.Defaults.Where(d => d.Value is TField f && f.Name.StartsWith("key.", StringComparison.Ordinal)).ToList();
                    var id = KeyId(policy, key, g);
                    if (en.Id is not null && en.Id.Length > 0 && en.Id != id) throw Reject("KeyMismatch", $"the key names {id}, the edit {en.Id}", g, en.Id);
                    edits[ei] = e with { Id = id, After = en with { Id = id } };
                    var props = new List<KeyValuePair<string, Value>>();
                    foreach (var d in policy.Defaults) props.Add(KeyValuePair.Create(d.PropId, DefaultValue(d.Value, key)));
                    foreach (var s in sink.Sets.Where(s => s.Value is TField f && f.Name.Contains('.')))
                    {
                        var pn = ((TField)s.Value).Name[(((TField)s.Value).Name.IndexOf('.') + 1)..];
                        var srcProp = view.PropsOf(src).Keys.First(k => view.Props[k].Name == pn);
                        if (en.Props.Get(s.PropId) is { } v) props.RemoveAll(p => p.Key == srcProp); else continue;
                        props.Add(KeyValuePair.Create(srcProp, v));
                    }
                    var existing = master.Entity(id);
                    var ne = new Entity(id, src, Props.Of(props));
                    if (existing is not null)
                    {
                        if (existing.Props.Text(view.Props.Values.First(p => p.Name == "status" && Ids.ModuleOf(p.Id) == Ids.ModuleOf(src)).Id) is not "retired" and not null) throw Reject("ConstraintViolation", $"{id} already exists", g, id);
                        var merged = existing.Props; foreach (var (k, v) in ne.Props.Items) merged = merged.With(k, v);
                        ne = existing with { Props = merged };
                        overlay.Add(new Edit(existing.Key, existing, ne));
                    }
                    else overlay.Add(new Edit(ne.Key, null, ne));
                }
                else
                {
                    var existing = master.Entity(en.Id) ?? throw Reject("BadEdit", $"{en.Id} is not in the master", g, en.Id);
                    // the put ends in authored rows through the emit of the master's bijective rule; that emit is a bijection only while one key of its domain names the element: after an alias or a merge two rows give it, and a put cannot say which (Def. 8.4, Theorem 8.2 (c))
                    if (Engine.Master.Typed.Ops.OfType<TEmitEntity>().FirstOrDefault(x => x.Rule == inv.Rule && x.TypeId == inv.EntityTypeId && !x.Update)?.EntityId is TId tid)
                    {
                        var keys = Engine.Identities.Bindings().Where(b => b.Domain.Id == tid.DomainId && b.Id == existing.Id).Select(b => string.Join(",", b.Components)).Order(StringComparer.Ordinal).ToList();
                        if (keys.Count > 1) throw Reject("AmbiguousKey", $"{existing.Id} has {keys.Count} keys in {tid.DomainName}: {string.Join("; ", keys)}", g, en.Id, rule.Name);
                    }
                    var view2 = Catalog.OntologyOf(Engine.Master.Typed.OutputModule);
                    var props = existing.Props;
                    if (kind == "delete")
                    {
                        if (policy.DeleteMode != "set") throw Reject("NotAccepted", "delete without 'on delete set' is not supported", g, en.Id, rule.Name);
                        foreach (var d in policy.DeleteSets) props = props.With(d.PropId, DefaultValue(d.Value, null));
                    }
                    else
                        foreach (var s in sink.Sets.Where(s => s.Value is TField f && f.Name.Contains('.')))
                        {
                            var pn = ((TField)s.Value).Name[(((TField)s.Value).Name.IndexOf('.') + 1)..];
                            var srcProp = view2.PropsOf(src).Keys.First(k => view2.Props[k].Name == pn);
                            if (((Entity)e.After!).Props.Get(s.PropId) is { } v) props = props.With(srcProp, v);
                        }
                    if (!props.Equals(existing.Props)) overlay.Add(new Edit(existing.Key, existing, existing with { Props = props }));
                }
            }
            else
            {
                var rl = (Rel)el;
                var sink = tp.Ops.OfType<TEmitRel>().FirstOrDefault(s => !s.Update && s.TypeId == rl.Type) ?? throw Reject("NotEditable", $"no rule emits {TypeLabel(Catalog, rl.Type)}", g, rl.Id);
                var rule = RuleNamed(tp, sink.Rule ?? "");
                var kind = e.Before is null ? "create" : e.After is null ? "delete" : "update";
                if (rule.Class != "lens" || rule.Policy is null || !rule.Policy.Accept.Contains(kind)) throw Reject("NotAccepted", $"rule {rule.Name} does not accept {kind}", g, rl.Id, rule.Name);
                var expectId = IdentityCatalog.RelationshipId(options.Tenant, tp.OutputModule, sink.Key, rl.Source, rl.Target);
                if (kind == "create" && rl.Id.Length > 0 && rl.Id != expectId) throw Reject("KeyMismatch", $"the relationship id is {expectId}", g, rl.Id);
                var mrel = IdentityCatalog.RelationshipId(options.Tenant, Engine.Master.Typed.OutputModule, MasterRelKey(inv), rl.Source, rl.Target);
                if (kind == "create") edits[ei] = e with { Id = expectId, After = rl with { Id = expectId } };
                if (kind == "create") overlay.Add(Edit.Create(new Rel(mrel, rl.Source, rl.Target, inv.RelTypeId, Props.Empty)));
                else if (master.Rel(mrel) is { } old) overlay.Add(Edit.Remove(old));
                else throw Reject("BadEdit", $"{rl.Id} has no master relationship", g, rl.Id);
            }
        }
    }

    string MasterRelKey(RowInverse inv) => Engine.Master.Typed.Ops.OfType<TEmitRel>().First(e => e.Rule == inv.Rule && e.TypeId == inv.RelTypeId && !e.Update).Key;

    static IEnumerable<string> ChangedNames(TypedProjection tp, TEmitEntity sink, Entity before, Entity after)
    {
        foreach (var s in sink.Sets) if (!Equals(before.Props.Get(s.PropId), after.Props.Get(s.PropId))) yield return s.PropName;
    }

    string KeyId(TPolicy policy, JsonNode key, string g)
    {
        var dom = Catalog.Graph.Entity(policy.CreateDomainId!)!;
        var d = new DefView(Catalog.Graph);
        var comps = d.Children(policy.CreateDomainId!, "component");
        var comp = key["components"]?.AsObject() ?? throw Reject("KeyMismatch", "key.components is required", g);
        var codes = new long[comps.Count];
        for (var i = 0; i < comps.Count; i++)
        {
            var v = comp[Ids.Name(comps[i])]?.GetValue<string>() ?? throw Reject("KeyMismatch", $"missing key component {Ids.Name(comps[i])}", g);
            var canon = d.Target(comps[i], "canonicalizer") is { } c ? Ids.Name(c)["canon.".Length..] : "Exact";
            codes[i] = interner.Intern(IdentityCatalog.Canonicalize(canon, v) ?? throw Reject("KeyMismatch", "malformed key component", g));
        }
        _ = dom;
        var domain = Engine.Identities.Domain(policy.CreateDomainId!, policy.CreateDomainName ?? "", [.. comps.Select(Ids.Name)], [.. comps.Select(c => d.Target(c, "canonicalizer") is { } cz ? Ids.Name(cz)["canon.".Length..] : "Exact")], null);
        return interner.Get(Engine.Identities.Resolve(domain, codes));
    }

    static Value DefaultValue(TExpr e, JsonNode? key) => e switch
    {
        TLit l => l.Value,
        TField f when f.Name.StartsWith("key.", StringComparison.Ordinal) && key?["components"]?[f.Name[4..]] is { } v => Value.Text(v.GetValue<string>()),
        _ => throw new HostError("BadEdit", "unsupported default expression", 422),
    };

    /// <summary>The first difference between the edited view and the staged forward view on the elements the lens rules own, or null.</summary>
    string? OwnedDiff(TypedProjection tp, string g, Graph expected, Graph actual, bool isMaster)
    {
        var types = isMaster ? [InverseOf(tp)!.EntityTypeId, InverseOf(tp)!.RelTypeId] : tp.Rules.Where(r => r.Class is "lens" or "bijective").SelectMany(r => tp.Ops.Where(o => (o is TEmitEntity { Update: false } ee && ee.Rule == r.Name) || (o is TEmitRel { Update: false } er && er.Rule == r.Name))
            .Select(o => o is TEmitEntity x ? x.TypeId : ((TEmitRel)o).TypeId)).ToList();
        var ownedProps = tp.Ops.OfType<TEmitEntity>().Where(e => e.Rule is not null && tp.Rules.First(r => r.Name == e.Rule).Class is "lens" or "bijective").SelectMany(e => e.Sets.Where(s => !s.Telemetry).Select(s => s.PropId)).ToHashSet();
        string? Sig(Element e) => e switch
        {
            Entity en => en.Id + "|" + string.Join(",", en.Props.Items.Where(kv => ownedProps.Contains(kv.Key)).Select(kv => kv.Key + "=" + Jcs.Encode(kv.Value))),
            Rel r => r.Id + "|" + r.Source + ">" + r.Target,
            _ => null,
        };
        var a = expected.Elements().Where(e => (e is Entity en && types.Contains(en.Type)) || (e is Rel r && types.Contains(r.Type))).ToDictionary(e => e.Key, Sig);
        var b = actual.Elements().Where(e => (e is Entity en && types.Contains(en.Type)) || (e is Rel r && types.Contains(r.Type))).ToDictionary(e => e.Key, Sig);
        foreach (var k in a.Keys.Order()) if (!b.TryGetValue(k, out var s) || s != a[k]) return k.Id;
        foreach (var k in b.Keys.Order()) if (!a.ContainsKey(k)) return k.Id;
        return null;
    }
}

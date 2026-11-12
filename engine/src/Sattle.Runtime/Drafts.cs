using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

sealed class Draft
{
    public required string Id; public required long Base; public required Graph Graph; public List<Patch> Patches = [];
    public long Revision => Patches.Count;
}

/// <summary>Drafts (an overlay on the catalog), structural edits, releases and previews (PROTOCOL.md v3). A draft's canonical text is the printer's output, so text and graph stay two views of one definition graph.</summary>
public sealed partial class SpaceHost
{
    readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    int draftCounter;
    (long Rev, Dictionary<string, string> Texts)? headTexts;

    Draft GetDraft(string id) => drafts.TryGetValue(id, out var d) ? d : throw new HostError("UnknownDraft", $"no draft {id}", 404);

    static IEnumerable<string> ModulesOf(Graph g) => g.Entities.Where(e => Ids.Name(e.Type) == "Module" && e.Id != Kernel.ModuleId).Select(e => e.Id[2..]).Order(StringComparer.Ordinal);

    Dictionary<string, string> TextsOf(Graph g, List<Diagnostic>? problems = null)
    {
        var res = new Dictionary<string, string>();
        foreach (var m in ModulesOf(g))
        {
            try { res[m] = Printer.Print(g, m); }
            catch (UnprintableException e) { problems?.Add(new Diagnostic("Unprintable", "error", e.Element, e.Message)); }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or NullReferenceException or IndexOutOfRangeException or ArgumentException or InvalidCastException)
            {
                problems?.Add(new Diagnostic("Unprintable", "error", Ids.Module(m), $"module {m} cannot be printed: a declaration is incomplete ({e.GetType().Name})"));
            }
        }
        return res;
    }

    Dictionary<string, string> HeadTexts()
    {
        if (headTexts is { } h && h.Rev == catalogHistory.Count) return h.Texts;
        var t = TextsOf(Catalog.Graph);
        headTexts = (catalogHistory.Count, t);
        return t;
    }

    // ---- patch decoding (catalog graph, kernel names) ----
    static string TypeId(string name, bool rel) => rel ? Ids.KRel(name) : Ids.KEntity(name);
    static Element DecodeCatalog(JsonNode n)
    {
        var rel = n["source"] is not null;
        var type = TypeId(n["type"]!["value"]!.GetValue<string>(), rel);
        var props = Props.Of((n["properties"]?.AsArray() ?? []).Select(x => KeyValuePair.Create(Ids.KProp(x!["property"]!["value"]!.GetValue<string>()), DecodeValue(x["value"]!))));
        var id = n["id"]!["value"]!.GetValue<string>();
        return rel ? new Rel(id, n["source"]!["value"]!.GetValue<string>(), n["target"]!["value"]!.GetValue<string>(), type, props) : new Entity(id, type, props);
    }
    static Patch DecodePatch(JsonNode p, long from)
    {
        var edits = (p["edits"]?.AsArray() ?? []).Select(e =>
        {
            var b = e!["before"] is { } bb ? DecodeCatalog(bb) : null; var a = e["after"] is { } aa ? DecodeCatalog(aa) : null;
            return new Edit((a ?? b)!.Key, b, a);
        }).ToList();
        // one edit per element: a remove followed by a create of the same id is one change from the first before to the last after
        var net = new List<Edit>(); var at = new Dictionary<ElementKey, int>();
        foreach (var e in edits)
        {
            if (at.TryGetValue(e.Key, out var i)) net[i] = new Edit(e.Key, net[i].Before, e.After);
            else { at[e.Key] = net.Count; net.Add(e); }
        }
        return new Patch("catalog", from, from + 1, [.. net.Where(e => e.Effective)]);
    }

    // ---- lifecycle ----
    public JsonNode CreateDraft(long? baseRevision)
    {
        lock (Gate)
        {
            var b = baseRevision ?? catalogHistory.Count;
            if (b < 0 || b > catalogHistory.Count) throw new HostError("UnknownRevision", $"catalog head is {catalogHistory.Count}", 404);
            var d = new Draft { Id = "d" + (++draftCounter).ToString(CultureInfo.InvariantCulture), Base = b, Graph = b == catalogHistory.Count ? Catalog.Graph.Clone() : StateAt("catalog", b) };
            drafts[d.Id] = d;
            return DraftInfo(d.Id);
        }
    }
    public JsonNode DraftInfo(string id) { lock (Gate) { var d = GetDraft(id); return new JsonObject { ["draft"] = d.Id, ["baseRevision"] = Rev(d.Base), ["revision"] = Rev(d.Revision) }; } }
    public JsonNode ListDrafts() { lock (Gate) return new JsonArray([.. drafts.Values.Select(d => DraftInfo(d.Id))]); }
    public void DiscardDraft(string id) { lock (Gate) { GetDraft(id); drafts.Remove(id); } }
    public JsonNode DraftSnapshot(string id) { lock (Gate) { var d = GetDraft(id); return catalogExport.SnapshotOf(d.Graph, catalogExport.CatalogDescriptor(d.Revision)); } }
    public IReadOnlyList<JsonNode> DraftPatches(string id, long after) { lock (Gate) { var d = GetDraft(id); return [.. d.Patches.Where(p => p.To > after).Select(p => catalogExport.Patch(p))]; } }
    public string DraftText(string id, string module)
    {
        lock (Gate) { var d = GetDraft(id); var t = TextsOf(d.Graph); return t.TryGetValue(module, out var x) ? x : throw new HostError("UnknownModule", $"no module {module} in the draft", 404); }
    }

    /// <summary>Applies a structural edit (or the inverse of an earlier one) to the draft. from and to of the patch are informational; the guards decide.</summary>
    public JsonNode ApplyDraftPatch(string id, JsonNode patch, long expectedRevision)
    {
        lock (Gate)
        {
            var d = GetDraft(id);
            if (expectedRevision != d.Revision) throw new HostError("StaleRevision", $"draft {id} is at revision {d.Revision}", 409, new JsonObject { ["actual"] = Rev(d.Revision) });
            Patch p;
            try { p = DecodePatch(patch, d.Revision); }
            catch (Exception e) when (e is KeyNotFoundException or NullReferenceException or InvalidOperationException or FormatException) { throw new HostError("BadRequest", "malformed patch: " + e.Message, 400); }
            string? err;
            try { err = d.Graph.Apply(p.Edits); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException) { throw new HostError("BadPatch", "the patch cannot be applied: " + e.Message, 422); }
            if (err is not null) throw new HostError("GuardFailed", err, 409, new JsonObject { ["element"] = err.Split(' ').Skip(1).FirstOrDefault()?.TrimEnd(':') });
            d.Patches.Add(p);
            var res = CheckDraftLocked(d);
            Publish(new JsonObject { ["type"] = "draft", ["draft"] = id, ["revision"] = Rev(d.Revision), ["ok"] = res["ok"]!.DeepClone(), ["diagnostics"] = res["diagnostics"]!.DeepClone() }, null);
            return res;
        }
    }

    public JsonNode CheckDraft(string id) { lock (Gate) return CheckDraftLocked(GetDraft(id)); }

    /// <summary>What a draft changes: its module texts that differ from the texts at its base revision. A module that only the head changed since is not a change of the draft.</summary>
    Dictionary<string, string> ChangesOf(Draft d, Dictionary<string, string> texts)
    {
        var baseTexts = d.Base == catalogHistory.Count ? HeadTexts() : TextsOf(StateAt("catalog", d.Base));
        return texts.Where(kv => !baseTexts.TryGetValue(kv.Key, out var b) || b != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    JsonObject CheckDraftLocked(Draft d)
    {
        var problems = new List<Diagnostic>();
        var texts = TextsOf(d.Graph, problems);
        var changed = ChangesOf(d, texts);
        var diags = new JsonArray([.. problems.Select(DiagJson)]);
        var ok = problems.Count == 0;
        JsonNode schemas = ok ? Schemas(Catalog) : new JsonObject();          // unchanged: the head catalog's
        if (ok && changed.Count > 0)
        {
            var r = (JsonObject)CheckTexts(changed);
            foreach (var x in r["diagnostics"]!.AsArray()) diags.Add(x!.DeepClone());
            ok = r["ok"]!.GetValue<bool>();
            schemas = r["schemas"]!.DeepClone();
        }
        var modules = new JsonObject();
        foreach (var (m, t) in texts) modules[m] = new JsonObject { ["text"] = t, ["changed"] = changed.ContainsKey(m) };
        return new JsonObject { ["revision"] = Rev(d.Revision), ["ok"] = ok, ["diagnostics"] = diags, ["modules"] = modules, ["schemas"] = schemas };
    }

    public JsonNode CommitDraft(string id, long expectedCatalogRevision)
    {
        lock (Gate)
        {
            var d = GetDraft(id);
            var problems = new List<Diagnostic>();
            var texts = TextsOf(d.Graph, problems);
            if (problems.Count > 0) throw new HostError("Invalid", "the draft cannot be printed", 422, new JsonObject { ["diagnostics"] = new JsonArray([.. problems.Select(DiagJson)]) });
            var baseTexts = d.Base == catalogHistory.Count ? HeadTexts() : TextsOf(StateAt("catalog", d.Base));
            var touched = texts.Where(kv => !baseTexts.TryGetValue(kv.Key, out var b) || b != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            var head = HeadTexts();
            foreach (var m in touched.Keys)
                if (d.Base != catalogHistory.Count && head.TryGetValue(m, out var h) && baseTexts.TryGetValue(m, out var b0) && h != b0)
                    throw new HostError("StaleRevision", $"module {m} changed after the draft's base revision", 409, new JsonObject { ["actual"] = Rev(catalogHistory.Count), ["module"] = m });
            if (touched.Count == 0) return new JsonObject { ["catalogRevision"] = Rev(catalogHistory.Count), ["modules"] = new JsonArray(), ["activation"] = new JsonObject { ["graphs"] = new JsonArray(), ["replayedRows"] = "0", ["milliseconds"] = "0" } };
            var res = (JsonObject)CommitTexts(touched, expectedCatalogRevision);
            res["modules"] = new JsonArray([.. touched.Keys.Order().Select(m => (JsonNode)m)]);
            drafts.Remove(id);
            return res;
        }
    }

    public JsonNode Skeleton(JsonNode body)
    {
        lock (Gate)
        {
            var values = body["params"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.ToString());
            var rev = body["draft"] is { } dn ? GetDraft(dn.GetValue<string>()).Revision : catalogHistory.Count;
            var p = Palette.Skeleton(body["kind"]!.GetValue<string>(), body["container"]!.GetValue<string>(), body["name"]!.GetValue<string>(), values, rev, out var placeholders);
            var answer = catalogExport.Patch(p);
            answer["placeholders"] = new JsonArray([.. placeholders.Select(x => (JsonNode)new JsonObject { ["id"] = new JsonObject { ["value"] = x.Id }, ["slot"] = x.Slot })]);
            return answer;
        }
    }

    // ---- releases ----
    sealed record Release(string Module, string Version, string Digest, long Revision, string? Supersedes);

    List<Release> ReleaseList()
    {
        var res = new List<Release>(); var last = new Dictionary<string, string>();
        foreach (var p in catalogHistory)
            foreach (var e in p.Edits)
                if (e.After is Entity en && Ids.Name(en.Type) == "Release" && e.Key.Id.StartsWith("d:", StringComparison.Ordinal))
                {
                    var module = Ids.ModuleOf(e.Key.Id);
                    var digest = en.Props.Text(Ids.KProp("digest"))!;
                    if (last.TryGetValue(module, out var prev) && prev == digest) continue;
                    res.Add(new Release(module, en.Props.Text(Ids.KProp("version"))!, digest, p.To, last.GetValueOrDefault(module)));
                    last[module] = digest;
                }
        return res;
    }

    public JsonNode Releases(string? module)
    {
        lock (Gate) return new JsonArray([.. ReleaseList().Where(r => module is null || r.Module == module).Select(r => (JsonNode)new JsonObject
        {
            ["module"] = r.Module, ["version"] = r.Version, ["digest"] = r.Digest, ["catalogRevision"] = Rev(r.Revision), ["supersedes"] = r.Supersedes,
        })]);
    }

    Release FindRelease(string digest) => ReleaseList().FirstOrDefault(r => r.Digest == digest) ?? throw new HostError("UnknownRelease", $"no release {digest}", 404);

    /// <summary>The definition facts of one module: its nodes, and the edges that leave them.</summary>
    static Graph ModuleFacts(Graph g, string module)
    {
        var res = new Graph(); var prefix = "d:" + module + "/"; var mid = "d:" + module;
        var edits = new List<Edit>();
        foreach (var e in g.Entities.Where(e => e.Id == mid || e.Id.StartsWith(prefix, StringComparison.Ordinal))) edits.Add(Edit.Create(e));
        foreach (var r in g.Rels.Where(r => r.Source == mid || r.Source.StartsWith(prefix, StringComparison.Ordinal))) edits.Add(Edit.Create(r));
        res.Apply(edits);
        return res;
    }

    Graph FactsAt(Release r) => ModuleFacts(StateAt("catalog", r.Revision), r.Module);

    public JsonNode ReleaseSnapshot(string digest) { lock (Gate) { var r = FindRelease(digest); return catalogExport.SnapshotOf(FactsAt(r), catalogExport.CatalogDescriptor(r.Revision)); } }
    public string ReleaseText(string digest) { lock (Gate) { var r = FindRelease(digest); return Printer.Print(StateAt("catalog", r.Revision), r.Module); } }
    public JsonNode ReleaseDiff(string from, string to)
    {
        lock (Gate)
        {
            var a = FindRelease(from); var b = FindRelease(to);
            return catalogExport.Patch(new Patch("catalog", a.Revision, b.Revision, Graph.Diff(FactsAt(a), FactsAt(b))));
        }
    }

    public JsonNode Restore(string id, string digest)
    {
        lock (Gate)
        {
            var d = GetDraft(id); var r = FindRelease(digest);
            var edits = Graph.Diff(ModuleFacts(d.Graph, r.Module), FactsAt(r));
            var patch = catalogExport.Patch(new Patch("catalog", d.Revision, d.Revision + 1, edits));
            var res = (JsonObject)ApplyDraftPatch(id, patch, d.Revision);
            res["patch"] = patch.DeepClone();
            return res;
        }
    }

    /// <summary>The premises of Proposition 5.7 that the refactored catalog breaks, so the union of the outputs may change: R3 (a sink of the moved part and one of the kept part emit one type, so one key may be held twice or disagree, or one relationship tuple appear under two key rules), R4 (another graph reads the parent graph and a type that moved out of it), R5 (a rule moved between projections with different output ontologies, so relationship ids change).</summary>
    static List<(string Premise, string Reason)> BrokenPremises(Catalog cand, string kind, string module, JsonNode body)
    {
        var res = new List<(string, string)>();
        string Pid(string name) => $"d:{module}/projection:{name}";
        static IEnumerable<string> Created(TypedProjection tp) =>
            tp.Ops.OfType<TEmitEntity>().Where(e => !e.Update).Select(e => e.TypeId).Concat(tp.Ops.OfType<TEmitRel>().Where(e => !e.Update).Select(e => e.TypeId)).Distinct();
        // where the keys of an entity sink come from: its identity domain, or the type of the element whose id it reuses; two sinks with one key space can emit one key, whatever type each gives it
        static IEnumerable<string> KeySpaces(TypedProjection tp) => tp.Ops.OfType<TEmitEntity>().Where(e => !e.Update)
            .Select(e => e.EntityId is TId id ? "domain " + Ids.Name(id.DomainId) : "the ids of " + Ids.Name(e.EntityId.Type.Ty.TypeId ?? "")).Distinct();
        if (kind == "extract" && cand.Projections.TryGetValue(Pid(body["projection"]!.GetValue<string>()), out var parent) && cand.Projections.TryGetValue(Pid(body["name"]!.GetValue<string>()), out var moved))
        {
            foreach (var t in Created(moved).Intersect(Created(parent)).Order(StringComparer.Ordinal))
                res.Add(("R3", $"the extracted and the kept statements both emit {Ids.Name(t)}: a key emitted by both is held by two graphs, and values or relationship tuples that one graph resolved may disagree"));
            foreach (var k in KeySpaces(moved).Intersect(KeySpaces(parent)).Order(StringComparer.Ordinal))
                res.Add(("R3", $"the extracted and the kept statements both take entity keys from {k}: one key emitted by both, as two types or with two values, is one element in one graph and two in two"));
            foreach (var space in cand.Spaces.Values)
                foreach (var pg in space.Graphs.Where(g => g.ProjectionId == parent.Id))
                    foreach (var reader in space.Graphs.Where(g => g.Bindings.Values.Contains(pg.Name)))
                    {
                        var tp = reader.Instance ?? cand.Projections[reader.ProjectionId];
                        var read = tp.Ops.OfType<TMatch>().SelectMany(m => m.Nodes.Select(n => n.TypeId).Concat(m.Edges.Select(e => e.TypeId))).ToHashSet(StringComparer.Ordinal);
                        foreach (var t in Created(moved).Where(read.Contains).Order(StringComparer.Ordinal))
                            res.Add(("R4", $"graph {reader.Name} reads {Ids.Name(t)} from graph {pg.Name}, which no longer holds it"));
                    }
        }
        if (kind == "moveRule" && cand.Projections.TryGetValue(Pid(body["from"]!.GetValue<string>()), out var from) && cand.Projections.TryGetValue(Pid(body["to"]!.GetValue<string>()), out var to) && from.OutputModule != to.OutputModule)
            res.Add(("R5", $"the rule moves from output ontology {from.OutputModule} to {to.OutputModule}: the ids of its relationships change"));
        return res;
    }

    /// <summary>A refactoring is a pure function on the draft's canonical text; the result is the draft patch that realises it (not applied).</summary>
    public JsonNode Refactor(string id, JsonNode body)
    {
        lock (Gate)
        {
            var d = GetDraft(id);
            var problems = new List<Diagnostic>();
            var texts = TextsOf(d.Graph, problems);
            if (problems.Count > 0) throw new HostError("Invalid", "the draft cannot be printed", 422, new JsonObject { ["diagnostics"] = new JsonArray([.. problems.Select(DiagJson)]) });
            string S(string k) => body[k]?.GetValue<string>() ?? throw new HostError("BadRequest", $"{k} is required", 400);
            Dictionary<string, string> edited;
            try
            {
                edited = S("kind") switch
                {
                    "extract" => Sattle.Language.Refactor.Extract(texts, S("module"), S("projection"), [.. (body["select"]?.AsArray() ?? throw new HostError("BadRequest", "select is required", 400)).Select(x => x!.GetValue<string>())], S("name")),
                    "inline" => Sattle.Language.Refactor.Inline(texts, S("module"), S("projection"), S("into")),
                    "moveRule" => Sattle.Language.Refactor.MoveRule(texts, S("module"), S("from"), S("to"), S("rule")),
                    var k => throw new HostError("BadRequest", $"unknown refactoring {k}", 400),
                };
            }
            catch (RefactorError e) { throw new HostError(e.Code, e.Message, 422, new JsonObject { ["element"] = e.Element, ["include"] = new JsonArray([.. e.Include.Select(x => (JsonNode)x)]) }); }
            var all = new Dictionary<string, string>(texts);
            foreach (var (m, t) in edited) all[m] = t;
            var changed = ChangesOf(d, all);
            var cand = Candidate(changed, out var res, out _);
            if (res.HasErrors) throw new HostError("RefactorInvalid", "the refactored text does not check", 422, new JsonObject { ["diagnostics"] = new JsonArray([.. res.Diagnostics.Select(DiagJson)]) });
            var patch = catalogExport.Patch(new Patch("catalog", d.Revision, d.Revision + 1, Graph.Diff(d.Graph, cand.Graph)));
            var modules = new JsonObject();
            foreach (var m in edited.Keys.Order()) modules[m] = new JsonObject { ["text"] = Printer.Print(cand.Graph, m) };
            var premises = BrokenPremises(cand, S("kind"), S("module"), body);
            var preserves = premises.Count == 0 ? new JsonObject { ["outputs"] = "union" }
                : new JsonObject { ["outputs"] = "not preserved", ["premises"] = new JsonArray([.. premises.Select(p => (JsonNode)new JsonObject { ["premise"] = p.Premise, ["reason"] = p.Reason })]) };
            return new JsonObject { ["patch"] = patch, ["modules"] = modules, ["preserves"] = preserves, ["diagnostics"] = new JsonArray([.. res.Diagnostics.Select(DiagJson)]) };
        }
    }

    // ---- preview ----
    public JsonNode Preview(string id, JsonNode body)
    {
        lock (Gate)
        {
            var d = GetDraft(id);
            var problems = new List<Diagnostic>();
            var texts = TextsOf(d.Graph, problems);
            var changed = ChangesOf(d, texts);
            var cand = Candidate(changed, out var res, out _);
            var diags = problems.Select(DiagJson).Concat(res.Diagnostics.Select(DiagJson)).ToList();
            if (problems.Count > 0 || res.HasErrors) throw new HostError("Invalid", "the draft does not check", 422, new JsonObject { ["diagnostics"] = new JsonArray([.. diags]) });
            var newSpace = cand.Spaces.Keys.FirstOrDefault(k => Ids.Name(k) == Space.Name) ?? throw new HostError("ActivationRejected", $"graph space {Space.Name} no longer activates", 422);
            var bad = AdapterRegistry.Verify(cand, cand.Spaces[newSpace], Adapters);
            if (bad.Count > 0) throw new HostError("ActivationRejected", string.Join("; ", bad.Select(b => b.Message)), 422);
            var maxRows = long.TryParse(body["scope"]?["maxRows"]?.GetValue<string>(), out var mr) ? mr : 500_000;
            if (maxRows > 5_000_000) throw new HostError("ScopeTooLarge", "maxRows is capped at 5,000,000", 422);
            if (!retained) throw new HostError("InputsNotRetained", "the retained inputs exceed the cap", 422);
            var sw = Stopwatch.StartNew();
            using var fresh = new Engine(cand, newSpace, new EngineOptions { Tenant = options.Tenant, LateHorizonWindows = options.LateHorizonWindows }, interner);   // own in-memory stores: the live ones are not touched; the late horizon of the live engine
            var include = (body["include"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet()) ?? ["counts", "diff"];
            var want = (body["graphs"]?.AsArray().Select(x => x!.GetValue<string>()).ToHashSet()) ?? fresh.Graphs.Select(g => g.Name).ToHashSet();
            if (include.Contains("rows")) fresh.TraceGraphs = want;
            long rows = 0; var complete = true;
            foreach (var r in inputs)
            {
                if (rows >= maxRows) { complete = false; break; }
                switch (r.Kind) { case 'r': fresh.Ingest(r.Source, r.Rows!, r.Seq); rows += r.Value; break; case 'a': fresh.Advance(r.Value, r.Seq); break; case 'g': fresh.Progress(r.Source, r.Value, r.Seq); break; case 'l': fresh.RecordLoss(r.Value, r.Seq, r.Source.Length == 0 ? "sourceLoss" : r.Source); break; }
            }
            var exp = new CanvasExport(fresh, cand);
            var limit = int.TryParse(body["limit"]?.GetValue<string>(), out var lm) ? lm : 5000;
            var mem = (InMemoryTelemetryStore)fresh.Telemetry;
            var graphs = new JsonArray();
            foreach (var g in fresh.Graphs.Where(g => want.Contains(g.Name)))
            {
                var o = new JsonObject
                {
                    ["id"] = g.Name, ["digest"] = g.State.Digest(),
                    ["counts"] = new JsonObject { ["entities"] = Rev(g.State.EntityCount), ["relationships"] = Rev(g.State.RelCount), ["telemetryWindows"] = Rev(mem.Dump(g.Key).Count) },
                };
                if (include.Contains("diff"))
                {
                    var cur = Engine.Graphs.FirstOrDefault(x => x.Name == g.Name);
                    o["diff"] = complete ? exp.Patch(new Patch(g.Name, served.TryGetValue(g.Name, out var sg) ? sg.Head : 0, (served.TryGetValue(g.Name, out var sg2) ? sg2.Head : 0) + 1, Graph.Diff(cur?.State ?? new Graph(), g.State))) : null;
                }
                var truncated = false;
                if (include.Contains("snapshots"))
                {
                    var snap = (JsonObject)exp.Snapshot(g);
                    var ents = snap["extent"]!["entities"]!.AsArray(); var rels = snap["extent"]!["relationships"]!.AsArray();
                    if (ents.Count + rels.Count > limit) { truncated = true; while (ents.Count > limit) ents.RemoveAt(ents.Count - 1); while (rels.Count > Math.Max(0, limit - ents.Count)) rels.RemoveAt(rels.Count - 1); }
                    o["snapshot"] = snap;
                }
                if (include.Contains("rows"))
                {
                    // the rows each operator holds after the replay: how many, and the first `rowLimit` of them (ordered by their text), with the operator's columns
                    var rowLimit = int.TryParse(body["rowLimit"]?.GetValue<string>(), out var rl) ? Math.Clamp(rl, 0, 1000) : 20;
                    var traced = fresh.Traced.GetValueOrDefault(g.Name) ?? [];
                    o["operators"] = new JsonArray([.. g.Plan.Ops.Select(op =>
                    {
                        var held = traced.GetValueOrDefault(op.Name) ?? [];
                        var names = op.OutLayout.Names;
                        JsonNode? Cell(object? v) => v switch { null => null, decimal dv => JsonValue.Create(Norm.Decimal(dv)), long lv => JsonValue.Create(lv.ToString(CultureInfo.InvariantCulture)), var x => JsonValue.Create(x.ToString()) };
                        var sample = held.OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(rowLimit).Select(kv => (JsonNode)new JsonObject
                        {
                            ["weight"] = kv.Value.Weight.ToString(CultureInfo.InvariantCulture),
                            ["cells"] = new JsonObject(names.Select((n, i) => KeyValuePair.Create(n, Cell(kv.Value.Cells[i])))),
                        });
                        return (JsonNode)new JsonObject
                        {
                            ["op"] = op.Typed.Id, ["name"] = op.Name, ["kind"] = op.Typed.Kind, ["columns"] = new JsonArray([.. names.Select(n => (JsonNode)n)]),
                            ["rows"] = Rev(held.Count), ["weight"] = Rev(held.Values.Sum(x => x.Weight)), ["sample"] = new JsonArray([.. sample]), ["truncated"] = held.Count > rowLimit,
                        };
                    })]);
                }
                if (include.Contains("telemetry")) { var tb = exp.TelemetryBatches(g).Take(limit).ToList(); truncated |= tb.Count == limit; o["telemetry"] = new JsonArray([.. tb]); }
                o["truncated"] = truncated;
                graphs.Add(o);
            }
            foreach (var g in Engine.Graphs.Where(g => !fresh.Graphs.Any(x => x.Name == g.Name) && want.Contains(g.Name))) graphs.Add(new JsonObject { ["id"] = g.Name, ["removed"] = true });
            return new JsonObject
            {
                ["scope"] = new JsonObject { ["rows"] = Rev(rows), ["totalRows"] = Rev(inputRows), ["complete"] = complete, ["logicalTime"] = fresh.LogicalTime == long.MinValue ? null : Norm.Instant(fresh.LogicalTime) },
                ["diagnostics"] = new JsonArray([.. diags]), ["graphs"] = graphs, ["milliseconds"] = Rev(sw.ElapsedMilliseconds),
                ["identity"] = IdentityEffects(fresh.Identities, Engine.Identities, complete),
            };
        }
    }

    /// <summary>The identity effects of a preview: how many identities the previewed space minted, aliased through links, merged, and refused as conflicts, next to the live space's counts. Identities are resolved once per space, not per graph, so the counters are the space's; `difference` (preview minus live) is the effect of the draft when `comparable` (the preview saw every retained input). A draft that adds a `Link` shows it as more `aliased` or `merged`.</summary>
    static JsonObject IdentityEffects(IdentityCatalog preview, IdentityCatalog live, bool comparable)
    {
        JsonObject Of(IdentityCatalog c) => new() { ["minted"] = Rev(c.Minted), ["aliased"] = Rev(c.Aliased), ["merged"] = Rev(c.Merges), ["conflicts"] = Rev(c.Conflicts) };
        var diff = new JsonObject { ["minted"] = Rev(preview.Minted - live.Minted), ["aliased"] = Rev(preview.Aliased - live.Aliased), ["merged"] = Rev(preview.Merges - live.Merges), ["conflicts"] = Rev(preview.Conflicts - live.Conflicts) };
        return new JsonObject { ["preview"] = Of(preview), ["live"] = Of(live), ["comparable"] = comparable, ["difference"] = comparable ? diff : null };
    }
}


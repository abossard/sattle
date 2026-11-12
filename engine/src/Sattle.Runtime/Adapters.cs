using System.Globalization;
using System.Text.Json;
using Sattle.Core;
using Sattle.Language;

namespace Sattle.Runtime;

/// <summary>One line of a capture: the envelope of lab/CAPTURE-FORMAT.md.</summary>
public sealed record Envelope(string Source, long CapturedNs, long Seq, string File, JsonElement Payload, JsonElement Meta, bool HasMeta);

/// <summary>Where adapters write rows. Rows of one type accumulate into a chunk; a row of another type flushes the current chunk first, so input order is kept across row types.</summary>
public interface IRowSink
{
    RowWriter Begin(string rowType);
    void Rejected(string source, string reason);
    /// <summary>The source lost events at timeNs (count 0 when unknown). reason names the kind of loss; the telemetry windows containing the time become Partial(reason).</summary>
    void SourceLoss(long timeNs, long count, string reason = "sourceLoss");
}

public sealed record RowField(string Name, string Kind, bool Optional);
public sealed record RowContract(string RowType, RowField[] Fields);

/// <summary>A compiled adapter: capture records in, typed rows out. Declares the row contracts it produces; activation checks them against the catalog (section 3.4).</summary>
public interface ISourceAdapter
{
    string Id { get; }
    IReadOnlyList<RowContract> Contracts { get; }
    bool Handles(string envelopeSource);
    void Consume(Envelope e, IRowSink sink);
}

/// <summary>An adapter for which a record that is delivered again can still say something. Live input drops an envelope whose key it has seen; it asks first whether this delivery counts.</summary>
public interface IRedelivery
{
    /// <summary>True when the record is to be consumed although its key was seen before. Reads the adapter's state, changes nothing.</summary>
    bool Counts(Envelope e);
}

public static class AdapterRegistry
{
    public static IReadOnlyList<ISourceAdapter> Create(string cluster) =>
        [new KubernetesAdapter(cluster), new HubbleAdapter(cluster), new AzureResourceGraphAdapter(), new PrometheusAdapter(cluster), new ActivityLogAdapter(), new AzureMetricsAdapter(), new VnetFlowLogAdapter(), new DiagnosticActivityAdapter(), new AksAuditAdapter(), new AuthoringAdapter()];

    /// <summary>Every Source.adapter must be registered and the row contract it declares must equal the contract in the catalog (the digest check of section 3.4, as a shape check so a definition change needs no rebuild).</summary>
    public static List<Diagnostic> Verify(Catalog catalog, TypedSpace space, IEnumerable<ISourceAdapter> adapters)
    {
        var res = new List<Diagnostic>();
        var d = new DefView(catalog.Graph);
        var byId = adapters.ToDictionary(a => a.Id);
        foreach (var s in space.Sources)
        {
            if (!byId.TryGetValue(s.Adapter, out var ad)) { res.Add(new Diagnostic("AdapterUnknown", "error", s.RowTypeId, $"source {s.Name}: adapter '{s.Adapter}' is not registered")); continue; }
            var name = Ids.Name(s.RowTypeId);
            var c = ad.Contracts.FirstOrDefault(x => x.RowType == name);
            if (c is null) { res.Add(new Diagnostic("AdapterContract", "error", s.RowTypeId, $"adapter {ad.Id} does not produce rows of type {name}")); continue; }
            var fields = d.Children(s.RowTypeId, "field").Select(f => new RowField(d.Text(f, "name")!, Ids.Name(d.Target(f, "valueKind")!), d.Bool(f, "optional"))).ToArray();
            if (!fields.SequenceEqual(c.Fields)) res.Add(new Diagnostic("AdapterContract", "error", s.RowTypeId, $"adapter {ad.Id} declares row contract {name}({string.Join(", ", c.Fields.Select(f => f.Name + (f.Optional ? "?" : "") + ":" + f.Kind))}) but the catalog has ({string.Join(", ", fields.Select(f => f.Name + (f.Optional ? "?" : "") + ":" + f.Kind))})"));
        }
        return res;
    }
}

static class J
{
    public static JsonElement? P(this JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    public static string? S(this JsonElement e, string name) => e.P(name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static JsonElement? Path(this JsonElement e, params string[] path) { JsonElement? cur = e; foreach (var p in path) { cur = cur?.P(p); if (cur is null) return null; } return cur; }
    public static string? PathS(this JsonElement e, params string[] path) => e.Path(path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

public static class RowDef
{
    public static RowField F(string n, string k, bool opt = false) => new(n, k, opt);
}

/// <summary>Kubernetes list and watch records, and Events. Object upserts become signed rows (retract the old row, insert the new one), deletes retract.</summary>
public sealed class KubernetesAdapter(string defaultCluster) : ISourceAdapter, IRedelivery
{
    /// <summary>The cluster of the record being consumed: the cluster the run was given, or the `cluster` of the envelope meta (a capture of several clusters).</summary>
    string cluster = "";
    public string Id => "kubernetes.list-watch";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("PodRow", [RowDef.F("cluster", "Text"), RowDef.F("uid", "Text"), RowDef.F("namespace", "Text"), RowDef.F("name", "Text"), RowDef.F("phase", "Text", true), RowDef.F("teamLabel", "Text", true), RowDef.F("nodeName", "Text", true), RowDef.F("restarts", "Integer", true), RowDef.F("ready", "Boolean", true), RowDef.F("serviceAccount", "Text", true), RowDef.F("deployment", "Text", true)]),
        new("DeploymentRow", [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("name", "Text"), RowDef.F("replicas", "Integer", true)]),
        new("ServiceRow", [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("name", "Text"), RowDef.F("externalName", "Text", true), RowDef.F("address", "Text", true)]),
        new("ServiceAccountRow", [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("name", "Text"), RowDef.F("clientId", "Text", true)]),
        new("HostReferenceRow", [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("deployment", "Text"), RowDef.F("variable", "Text"), RowDef.F("host", "Text")]),
        new("EndpointRow", [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("service", "Text"), RowDef.F("address", "Text"), RowDef.F("podUid", "Text", true)]),
        new("NodeRow", [RowDef.F("cluster", "Text"), RowDef.F("uid", "Text"), RowDef.F("name", "Text"), RowDef.F("resourceId", "Text", true), RowDef.F("pool", "Text", true)]),
        new("EventRow", [RowDef.F("eventId", "Text"), RowDef.F("time", "Instant"), RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("kind", "Text"), RowDef.F("name", "Text"), RowDef.F("reason", "Text"), RowDef.F("type", "Text"), RowDef.F("count", "Integer"), RowDef.F("message", "Text", true)]),
    ];
    public bool Handles(string s) => s is "kubernetes-list" or "kubernetes-watch" or "sattle-forwarder";

    /// <summary>The rows the adapter has emitted, per object. An object is known by its cluster and uid (`Key`); a row ends with the cluster it was written with, so a retraction repeats it whatever record causes it.</summary>
    readonly Dictionary<string, string?[]> pods = [], services = [], nodes = [], deployments = [], accounts = [];
    /// <summary>The host references of each Deployment (`HostReferenceRow`), by the Deployment's key: written again when the Deployment changes.</summary>
    readonly Dictionary<string, List<string?[]>> hostRefs = [];
    readonly Dictionary<string, List<string?[]>> slices = [];
    readonly Dictionary<string, long> eventCounts = [];
    public long Events { get; private set; }

    /// <summary>Full lists in progress per cluster and API path (between relist-start and relist-end): the objects seen so far.</summary>
    readonly Dictionary<string, HashSet<string>> relists = [];

    static string Key(string cluster, string id) => cluster + "\n" + id;
    string ClusterOf(Envelope e) => e.HasMeta && e.Meta.S("cluster") is { Length: > 0 } mc ? mc : defaultCluster;

    /// <summary>A record of a full list in progress says that its object exists, each time it is delivered: an unchanged object has the same key in every list.</summary>
    public bool Counts(Envelope e) => e.Source == "kubernetes-list" && e.HasMeta && e.Meta.S("path") is { } path && relists.ContainsKey(Key(ClusterOf(e), path));

    /// <summary>The forwarder's own records (lab/CAPTURE-FORMAT.md). A relist is the complete set of one kind: what the engine knows of that kind and the list did not mention is deleted. Loss markers become evidence on the telemetry windows that contain them.</summary>
    void Marker(Envelope e, IRowSink sink)
    {
        var p = e.Payload; var ev = p.S("event"); var path = p.S("path");
        var at = p.S("at") is { } t && Norm.TryParseInstant(t, out var ns) ? ns : e.CapturedNs;
        switch (ev)
        {
            case "relist-start" when path is not null: relists[Key(cluster, path)] = []; break;
            case "relist-end" when path is not null && relists.Remove(Key(cluster, path), out var seen):
                var mine = Key(cluster, "");
                List<string> Gone(IEnumerable<string> known) => [.. known.Where(k => k.StartsWith(mine, StringComparison.Ordinal) && !seen.Contains(k))];       // the list is the complete set of this cluster only
                switch (path[(path.LastIndexOf('/') + 1)..])
                {
                    case "pods": foreach (var k in Gone(pods.Keys)) Upsert(pods, k, null, sink, "PodRow", WritePod); break;
                    case "services": foreach (var k in Gone(services.Keys)) Upsert(services, k, null, sink, "ServiceRow", WriteService); break;
                    case "nodes": foreach (var k in Gone(nodes.Keys)) Upsert(nodes, k, null, sink, "NodeRow", WriteNode); break;
                    case "deployments": foreach (var k in Gone(deployments.Keys)) Upsert(deployments, k, null, sink, "DeploymentRow", WriteDeployment); break;
                    case "endpointslices": foreach (var k in Gone(slices.Keys)) RemoveSlice(k, sink); break;
                    case "serviceaccounts": foreach (var k in Gone(accounts.Keys)) Upsert(accounts, k, null, sink, "ServiceAccountRow", WriteAccount); break;
                }
                break;
            case "dropped": sink.SourceLoss(at, p.P("count") is { ValueKind: JsonValueKind.Number } c ? c.GetInt64() : 0, "forwarderDropped"); break;
            case "hubble-reconnect": sink.SourceLoss(at, 0, "hubbleReconnect"); break;
            case "poll-failed": sink.SourceLoss(at, 0, "pollFailed"); break;
        }
    }

    void RemoveSlice(string key, IRowSink sink)
    {
        if (!slices.Remove(key, out var prev)) return;
        foreach (var r in prev) { var w = sink.Begin("EndpointRow"); w.Begin(-1); Write(w, r); }
    }

    public void Consume(Envelope e, IRowSink sink)
    {
        cluster = ClusterOf(e);
        if (e.Source == "sattle-forwarder") { Marker(e, sink); return; }
        string type; string kind; JsonElement obj;
        if (e.Source == "kubernetes-list")
        {
            type = "ADDED";
            kind = (e.Meta.PathS("list", "kind") ?? "").Replace("List", "", StringComparison.Ordinal);
            obj = e.Payload;
        }
        else
        {
            type = e.Payload.S("type") ?? "";
            if (type is "BOOKMARK" or "ERROR" || e.Payload.P("object") is not { } o) return;
            obj = o; kind = obj.S("kind") ?? "";
        }
        var uid = obj.PathS("metadata", "uid"); var ns = obj.PathS("metadata", "namespace") ?? ""; var name = obj.PathS("metadata", "name");
        if (uid is null || name is null) return;
        var key = Key(cluster, uid);
        if (e.Source == "kubernetes-list" && e.Meta.S("path") is { } listPath && relists.TryGetValue(Key(cluster, listPath), out var inList)) inList.Add(key);
        var deleted = type == "DELETED";
        switch (kind)
        {
            case "Pod":
                Upsert(pods, key, deleted ? null : [uid, ns, name, obj.PathS("status", "phase"), obj.PathS("metadata", "labels", "team"), obj.PathS("spec", "nodeName"), Restarts(obj), ReadyOf(obj), DeploymentOf(obj), obj.PathS("spec", "serviceAccountName"), cluster], sink, "PodRow",
                    WritePod);
                break;
            case "Deployment":
                Upsert(deployments, key, deleted ? null : [uid, ns, name, obj.Path("spec", "replicas") is { ValueKind: JsonValueKind.Number } rp ? rp.GetInt64().ToString(CultureInfo.InvariantCulture) : null, cluster], sink, "DeploymentRow",
                    WriteDeployment);
                HostReferences(key, ns, name, deleted ? null : obj, sink);
                break;
            case "Service":
                Upsert(services, key, deleted ? null : [uid, ns, name, obj.PathS("spec", "externalName"), AddressOf(obj), cluster], sink, "ServiceRow", WriteService);
                break;
            case "Node":
                Upsert(nodes, key, deleted ? null : [uid, name, ResourceIdOf(obj.PathS("spec", "providerID")), obj.PathS("metadata", "labels", "kubernetes.azure.com/agentpool") ?? obj.PathS("metadata", "labels", "agentpool"), cluster], sink, "NodeRow", WriteNode);
                break;
            case "ServiceAccount":
                Upsert(accounts, key, deleted ? null : [uid, ns, name, obj.PathS("metadata", "annotations", "azure.workload.identity/client-id"), cluster], sink, "ServiceAccountRow", WriteAccount);
                break;
            case "EndpointSlice": Slice(key, ns, obj, deleted, sink); break;
            case "Event": Event(uid, ns, obj, sink); break;
        }
    }

    static void WritePod(RowWriter w, string?[] r) { w.Text("cluster", r[^1]); w.Text("uid", r[0]); w.Text("namespace", r[1]); w.Text("name", r[2]); w.Text("phase", r[3]); w.Text("teamLabel", r[4]); w.Text("nodeName", r[5]); if (r[6] is { } n) w.Int(w.Col("restarts"), long.Parse(n, CultureInfo.InvariantCulture)); else w.Absent(w.Col("restarts")); if (r[7] is { } rd) w.Code(w.Col("ready"), rd == "True" ? 1 : 0); else w.Absent(w.Col("ready")); w.Text("deployment", r[8]); w.Text("serviceAccount", r[9]); }
    static void WriteDeployment(RowWriter w, string?[] r) { w.Text("cluster", r[^1]); w.Text("namespace", r[1]); w.Text("name", r[2]); if (r[3] is { } rn) w.Int(w.Col("replicas"), long.Parse(rn, CultureInfo.InvariantCulture)); else w.Absent(w.Col("replicas")); }
    static void WriteService(RowWriter w, string?[] r) { w.Text("cluster", r[^1]); w.Text("namespace", r[1]); w.Text("name", r[2]); w.Text("externalName", r[3]); w.Text("address", r[4]); }
    static void WriteNode(RowWriter w, string?[] r) { w.Text("cluster", r[^1]); w.Text("uid", r[0]); w.Text("name", r[1]); w.Text("resourceId", r[2]); w.Text("pool", r[3]); }
    static void WriteAccount(RowWriter w, string?[] r) { w.Text("cluster", r[^1]); w.Text("namespace", r[1]); w.Text("name", r[2]); w.Text("clientId", r[3]); }
    static void WriteHostReference(RowWriter w, string?[] r) { w.Text("cluster", r[0]); w.Text("namespace", r[1]); w.Text("deployment", r[2]); w.Text("variable", r[3]); w.Text("host", r[4]); }

    /// <summary>The address a Service is reached at: its load balancer ingress, else its cluster IP. A headless Service has none.</summary>
    static string? AddressOf(JsonElement svc)
    {
        if (svc.Path("status", "loadBalancer", "ingress") is { ValueKind: JsonValueKind.Array } ing)
            foreach (var i in ing.EnumerateArray()) if (J.NullIfEmpty(i.S("ip") ?? i.S("hostname")) is { } a) return a;
        return svc.PathS("spec", "clusterIP") is { } ip && ip != "None" ? J.NullIfEmpty(ip) : null;
    }

    /// <summary>The host a value names: without a scheme, user, port or path (`postgres://u@db.example:5432/x` names `db.example`). Null for a value that is no host name or address.</summary>
    public static string? HostOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.IndexOf("://", StringComparison.Ordinal) is var s and >= 0) v = v[(s + 3)..];
        if (v.IndexOf('@') is var at and >= 0) v = v[(at + 1)..];
        var end = v.IndexOfAny(['/', '?', '#', ':', ';', ',']); if (end >= 0) v = v[..end];
        v = v.ToLowerInvariant();
        return v.Length > 0 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? v : null;
    }

    /// <summary>The environment variables of a Deployment's containers that name a host (by their name: `*HOST`, `*HOSTNAME`, `*SERVER`, `*ENDPOINT`, `*URL`, `*URI`), as one row per variable with the host it names. Other variables are not read: their values may be secrets.</summary>
    void HostReferences(string key, string ns, string deployment, JsonElement? obj, IRowSink sink)
    {
        var rows = new List<string?[]>();
        if (obj is { } o && o.Path("spec", "template", "spec", "containers") is { ValueKind: JsonValueKind.Array } containers)
            foreach (var c in containers.EnumerateArray())
                if (c.P("env") is { ValueKind: JsonValueKind.Array } env)
                    foreach (var v in env.EnumerateArray())
                        if (v.S("name") is { } n && new[] { "HOST", "HOSTNAME", "SERVER", "ENDPOINT", "URL", "URI" }.Any(x => n.EndsWith(x, StringComparison.OrdinalIgnoreCase)) && HostOf(v.S("value")) is { } host)
                            rows.Add([cluster, ns, deployment, n, host]);
        hostRefs.TryGetValue(key, out var prev); prev ??= [];
        foreach (var r in prev.Where(p => !rows.Any(x => x.SequenceEqual(p)))) { var w = sink.Begin("HostReferenceRow"); w.Begin(-1); WriteHostReference(w, r); }
        foreach (var r in rows.Where(x => !prev.Any(p => p.SequenceEqual(x)))) { var w = sink.Begin("HostReferenceRow"); w.Begin(1); WriteHostReference(w, r); }
        if (rows.Count == 0) hostRefs.Remove(key); else hostRefs[key] = rows;
    }

    /// <summary>The Azure resource id behind a node: the providerID without its azure:// scheme (the identity domain lowercases it).</summary>
    static string? ResourceIdOf(string? providerId) => providerId is not null && providerId.StartsWith("azure://", StringComparison.OrdinalIgnoreCase) ? providerId["azure://".Length..] : null;

    /// <summary>The Ready condition of a pod: "True", "False", or null when the pod has no such condition yet.</summary>
    static string? ReadyOf(JsonElement pod)
    {
        if (pod.Path("status", "conditions") is not { ValueKind: JsonValueKind.Array } cs) return null;
        foreach (var c in cs.EnumerateArray()) if (c.S("type") == "Ready") return c.S("status");
        return null;
    }

    /// <summary>The Deployment that owns the pod: its ReplicaSet is named after the Deployment and a pod-template hash.</summary>
    static string? DeploymentOf(JsonElement pod)
    {
        if (pod.Path("metadata", "ownerReferences") is not { ValueKind: JsonValueKind.Array } refs) return null;
        foreach (var o in refs.EnumerateArray())
            if (o.S("kind") == "ReplicaSet" && o.S("name") is { } rs && rs.LastIndexOf('-') is > 0 and var i) return rs[..i];
        return null;
    }

    static string? Restarts(JsonElement pod)
    {
        if (pod.Path("status", "containerStatuses") is not { ValueKind: JsonValueKind.Array } cs) return null;
        long n = 0; foreach (var c in cs.EnumerateArray()) if (c.P("restartCount") is { ValueKind: JsonValueKind.Number } r) n += r.GetInt64();
        return n.ToString(CultureInfo.InvariantCulture);
    }

    static void Upsert(Dictionary<string, string?[]> state, string key, string?[]? next, IRowSink sink, string rowType, Action<RowWriter, string?[]> write)
    {
        state.TryGetValue(key, out var prev);
        if (prev is not null && next is not null && prev.SequenceEqual(next)) return;
        if (prev is not null) { var w = sink.Begin(rowType); w.Begin(-1); write(w, prev); }
        if (next is not null) { var w = sink.Begin(rowType); w.Begin(1); write(w, next); state[key] = next; }
        else state.Remove(key);
    }

    void Slice(string key, string ns, JsonElement obj, bool deleted, IRowSink sink)
    {
        var service = obj.PathS("metadata", "labels", "kubernetes.io/service-name");
        var rows = new List<string?[]>();
        if (!deleted && service is not null && obj.P("endpoints") is { ValueKind: JsonValueKind.Array } eps)
            foreach (var ep in eps.EnumerateArray())
            {
                var podUid = ep.Path("targetRef") is { } tr && tr.S("kind") == "Pod" ? tr.S("uid") : null;
                if (ep.P("addresses") is { ValueKind: JsonValueKind.Array } addrs)
                    foreach (var a in addrs.EnumerateArray()) rows.Add([ns, service, a.GetString(), podUid, cluster]);
            }
        slices.TryGetValue(key, out var prev);
        prev ??= [];
        foreach (var r in prev.Where(p => !rows.Any(x => x.SequenceEqual(p)))) { var w = sink.Begin("EndpointRow"); w.Begin(-1); Write(w, r); }
        foreach (var r in rows.Where(x => !prev.Any(p => p.SequenceEqual(x)))) { var w = sink.Begin("EndpointRow"); w.Begin(1); Write(w, r); }
        if (rows.Count == 0) slices.Remove(key); else slices[key] = rows;
    }
    static void Write(RowWriter w, string?[] r) { w.Text("cluster", r[4]); w.Text("namespace", r[0]); w.Text("service", r[1]); w.Text("address", r[2]); w.Text("podUid", r[3]); }

    void Event(string uid, string ns, JsonElement obj, IRowSink sink)
    {
        var count = obj.P("count") is { ValueKind: JsonValueKind.Number } c ? c.GetInt64() : obj.Path("series", "count") is { ValueKind: JsonValueKind.Number } sc ? sc.GetInt64() : 1;
        var last = eventCounts.GetValueOrDefault(Key(cluster, uid));
        if (count <= last) return;
        eventCounts[Key(cluster, uid)] = count;
        var ts = obj.S("lastTimestamp") ?? obj.S("eventTime") ?? obj.PathS("series", "lastObservedTime") ?? obj.S("firstTimestamp") ?? obj.PathS("metadata", "creationTimestamp");
        if (ts is null || !Norm.TryParseInstant(ts, out var ns2)) { sink.Rejected("kubernetes-event", "no parseable timestamp"); return; }
        var inv = obj.P("involvedObject");
        if (inv is null) return;
        var w = sink.Begin("EventRow"); w.Begin();
        w.Text("eventId", uid + "/" + count); w.Text("message", obj.S("message")); w.Instant("time", ns2); w.Text("cluster", cluster); w.Text("namespace", inv.Value.S("namespace") ?? ns); w.Text("kind", inv.Value.S("kind") ?? ""); w.Text("name", inv.Value.S("name") ?? "");
        w.Text("reason", obj.S("reason") ?? ""); w.Text("type", obj.S("type") ?? ""); w.Code(w.Col("count"), count - last);
        Events++;
    }
}

/// <summary>Hubble flows, including lost-event records (which mark telemetry windows partial, Def. 7.7).</summary>
public sealed class HubbleAdapter(string cluster) : ISourceAdapter
{
    public string Id => "hubble.observe";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("FlowRow", [RowDef.F("time", "Instant"), RowDef.F("cluster", "Text"), RowDef.F("verdict", "Text"), RowDef.F("srcNamespace", "Text", true), RowDef.F("srcPod", "Text", true), RowDef.F("dstNamespace", "Text", true), RowDef.F("dstPod", "Text", true), RowDef.F("dstAddress", "Text", true), RowDef.F("dstName", "Text", true)]),
    ];
    public bool Handles(string s) => s == "hubble";
    public long Flows { get; private set; }
    public long LostEventRecords { get; private set; }
    public long LostEvents { get; private set; }
    int cTime = -1, cCluster, cVerdict, cSn, cSp, cDn, cDp, cDa, cDnm;

    public void Consume(Envelope e, IRowSink sink)
    {
        if (e.Payload.P("lost_events") is { } lost)
        {
            LostEventRecords++;
            // Hubble writes the count as a JSON string (a uint64 in protobuf JSON); a number is accepted too
            var n = lost.P("num_events_lost") switch
            {
                { ValueKind: JsonValueKind.Number } c => c.GetInt64(),
                { ValueKind: JsonValueKind.String } c when long.TryParse(c.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0,
            };
            LostEvents += n;
            var t = e.Payload.S("time");
            sink.SourceLoss(t is not null && Norm.TryParseInstant(t, out var ns) ? ns : e.CapturedNs, n, "sourceLoss");
            return;
        }
        if (e.Payload.P("flow") is not { } flow) { sink.Rejected("hubble", "record has neither flow nor lost_events"); return; }
        var time = flow.S("time") ?? e.Payload.S("time");
        if (time is null || !Norm.TryParseInstant(time, out var tns)) { sink.Rejected("hubble", "flow without a parseable time"); return; }
        var verdict = flow.S("verdict");
        if (verdict is null) { sink.Rejected("hubble", "flow without verdict"); return; }
        var src = flow.P("source"); var dst = flow.P("destination");
        var w = sink.Begin("FlowRow");
        if (cTime < 0) { cTime = w.Col("time"); cCluster = w.Col("cluster"); cVerdict = w.Col("verdict"); cSn = w.Col("srcNamespace"); cSp = w.Col("srcPod"); cDn = w.Col("dstNamespace"); cDp = w.Col("dstPod"); cDa = w.Col("dstAddress"); cDnm = w.Col("dstName"); }
        w.Begin();
        w.Instant(cTime, tns);
        w.Text(cCluster, J.NullIfEmpty(src?.S("cluster_name") ?? dst?.S("cluster_name")) ?? cluster);
        w.Text(cVerdict, verdict);
        w.Text(cSn, J.NullIfEmpty(src?.S("namespace"))); w.Text(cSp, J.NullIfEmpty(src?.S("pod_name")));
        w.Text(cDn, J.NullIfEmpty(dst?.S("namespace"))); w.Text(cDp, J.NullIfEmpty(dst?.S("pod_name")));
        // the address a flow went to, and the first name Hubble resolved for it: what a flow to a destination outside the cluster says about it
        w.Text(cDa, J.NullIfEmpty(flow.PathS("IP", "destination")));
        w.Text(cDnm, flow.P("destination_names") is { ValueKind: JsonValueKind.Array } names && names.GetArrayLength() > 0 ? KubernetesAdapter.HostOf(names[0].GetString()) : null);
        Flows++;
    }
}

/// <summary>Azure Resource Graph rows (resources and resource containers).</summary>
public sealed class AzureResourceGraphAdapter : ISourceAdapter
{
    public string Id => "azure.resource-graph";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("ResourceRow", [RowDef.F("id", "Text"), RowDef.F("name", "Text"), RowDef.F("type", "Text"), RowDef.F("location", "Text", true), RowDef.F("resourceGroup", "Text", true), RowDef.F("subscriptionId", "Text", true), RowDef.F("parentId", "Text", true),
            RowDef.F("address", "Text", true), RowDef.F("linkedId", "Text", true), RowDef.F("clientId", "Text", true), RowDef.F("principalId", "Text", true), RowDef.F("scope", "Text", true)]),
    ];
    public bool Handles(string s) => s == "arg";
    public long Resources { get; private set; }

    /// <summary>A resource in an AKS node resource group (mc_{group}_{cluster}_{region}) belongs to that cluster. The group name is the only link Resource Graph rows carry without their properties.</summary>
    static string? ClusterOf(JsonElement row)
    {
        var rg = row.S("resourceGroup"); var sub = row.S("subscriptionId");
        if (rg is null || sub is null || !rg.StartsWith("mc_", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = rg.Split('_');
        return parts.Length == 4 ? $"/subscriptions/{sub}/resourcegroups/{parts[1]}/providers/microsoft.containerservice/managedclusters/{parts[2]}" : null;
    }

    public void Consume(Envelope e, IRowSink sink)
    {
        var id = e.Payload.S("id"); var name = e.Payload.S("name"); var type = e.Payload.S("type");
        if (id is null || name is null || type is null) { sink.Rejected("arg", "row without id, name or type"); return; }
        var w = sink.Begin("ResourceRow"); w.Begin();
        w.Text("id", id); w.Text("name", name); w.Text("type", type);
        w.Text("location", J.NullIfEmpty(e.Payload.S("location"))); w.Text("resourceGroup", J.NullIfEmpty(e.Payload.S("resourceGroup"))); w.Text("subscriptionId", J.NullIfEmpty(e.Payload.S("subscriptionId")));
        var p = e.Payload.P("properties") is { ValueKind: JsonValueKind.Object } props ? props : (JsonElement?)null;
        string? Prop(params string[] path) => p is { } x ? J.NullIfEmpty(x.PathS(path)) : null;
        string? address = null, linked = null, client = null, principal = null, scope = null, parent = ClusterOf(e.Payload);
        switch (type.ToLowerInvariant())
        {
            case "microsoft.network/privateendpoints":
                // the address the endpoint is reached at, the resource it leads to, and the virtual network it sits in
                address = p?.P("customDnsConfigs") is { ValueKind: JsonValueKind.Array } dns && dns.GetArrayLength() > 0 && dns[0].P("ipAddresses") is { ValueKind: JsonValueKind.Array } ips && ips.GetArrayLength() > 0 ? ips[0].GetString() : null;
                linked = p?.P("privateLinkServiceConnections") is { ValueKind: JsonValueKind.Array } links && links.GetArrayLength() > 0 ? J.NullIfEmpty(links[0].PathS("properties", "privateLinkServiceId")) : null;
                if (Prop("subnet", "id") is { } subnet && subnet.IndexOf("/subnets/", StringComparison.OrdinalIgnoreCase) is var cut and > 0) parent = subnet[..cut];
                break;
            case "microsoft.apimanagement/service/backends":
                address = KubernetesAdapter.HostOf(Prop("url"));
                if (id.IndexOf("/backends/", StringComparison.OrdinalIgnoreCase) is var b and > 0) parent = id[..b];
                break;
            case "microsoft.managedidentity/userassignedidentities":
                client = Prop("clientId"); principal = Prop("principalId");
                break;
            case "microsoft.authorization/roleassignments":
                principal = Prop("principalId"); scope = Prop("scope");
                break;
        }
        w.Text("parentId", parent); w.Text("address", address); w.Text("linkedId", linked); w.Text("clientId", client); w.Text("principalId", principal); w.Text("scope", scope);
        Resources++;
    }
}

/// <summary>Prometheus range queries as metric rows: one row per sample of every series that has an instance label.</summary>
public sealed class PrometheusAdapter(string defaultCluster) : ISourceAdapter
{
    string cluster = "";
    public string Id => "prometheus.range";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("MetricRow", [RowDef.F("time", "Instant"), RowDef.F("cluster", "Text"), RowDef.F("metric", "Text"), RowDef.F("series", "Text"), RowDef.F("instance", "Text", true), RowDef.F("value", "Decimal")]),
    ];
    public bool Handles(string s) => s == "prometheus";
    public long Samples { get; private set; }
    public void Consume(Envelope e, IRowSink sink)
    {
        cluster = e.HasMeta && e.Meta.S("cluster") is { Length: > 0 } mc ? mc : defaultCluster;
        var metric = e.Meta.S("name");
        if (metric is null || e.Payload.Path("data", "result") is not { ValueKind: JsonValueKind.Array } result) { sink.Rejected("prometheus", "no metric name or result"); return; }
        foreach (var series in result.EnumerateArray())
        {
            if (series.P("values") is not { ValueKind: JsonValueKind.Array } values) continue;
            var (labels, instance) = (LabelsOf(series), series.PathS("metric", "instance"));       // a query that aggregates `instance` away has series all the same
            foreach (var v in values.EnumerateArray())
            {
                if (v.GetArrayLength() != 2 || !v[0].TryGetDouble(out var ts) || !decimal.TryParse(v[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var val)) continue; // NaN and gaps are not samples
                var w = sink.Begin("MetricRow"); w.Begin();
                w.Instant("time", (long)(ts * 1_000_000_000d)); w.Text("cluster", cluster); w.Text("metric", metric); w.Text("series", labels); w.Text("instance", instance); w.Dec(w.Col("value"), val);
                Samples++;
            }
        }
    }

    /// <summary>The label set of a series in Prometheus text form without the metric name, labels in order: what tells two series of one metric apart.</summary>
    static string LabelsOf(JsonElement series) => series.P("metric") is { ValueKind: JsonValueKind.Object } m
        ? string.Join(",", m.EnumerateObject().Where(l => l.Name != "__name__" && l.Value.ValueKind == JsonValueKind.String).OrderBy(l => l.Name, StringComparer.Ordinal)
            .Select(l => l.Name + "=\"" + l.Value.GetString()!.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""))
        : "";
}

/// <summary>Authored rows: capabilities and the services that realize them. A capture carries them as envelopes of source `authoring`, in capture-time order like every other record (a later `capability` row with the same capId revises the earlier one, a `capability` row with status `retired` retires it): payload `{ "kind": "capability", "capId", "name", "owner", "status", "parentId"? }` or `{ "kind": "realizes", "capId", "cluster", "namespace", "service" }`.</summary>
public sealed class AuthoringAdapter : ISourceAdapter
{
    public string Id => "sattle.authoring";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("CapabilityRow", [RowDef.F("capId", "Text"), RowDef.F("name", "Text"), RowDef.F("owner", "Text"), RowDef.F("status", "Text"), RowDef.F("parentId", "Text", true)]),
        new("RealizesRow", [RowDef.F("capId", "Text"), RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("service", "Text")]),
        new("SignalRuleRow", [RowDef.F("ruleId", "Text"), RowDef.F("type", "Text"), RowDef.F("meter", "Text"), RowDef.F("above", "Decimal"), RowDef.F("enterAfter", "Integer"), RowDef.F("below", "Decimal"), RowDef.F("exitAfter", "Integer"), RowDef.F("severity", "Integer"), RowDef.F("invert", "Boolean")]),
        new("PolicyRow", [RowDef.F("target", "Text"), RowDef.F("mode", "Text"), RowDef.F("share", "Decimal", true)]),
        new("CurationRow", [RowDef.F("curId", "Text"), RowDef.F("action", "Text"), RowDef.F("target", "Text"), RowDef.F("value", "Text", true)]),
    ];
    public bool Handles(string s) => s == "authoring";
    public long Rows { get; private set; }

    readonly record struct Cell(string Col, char Kind, string? Text, decimal Dec, long Int);
    static Cell T(string col, string? v) => new(col, 't', v, 0, 0);
    static Cell D(string col, decimal v) => new(col, 'd', null, v, 0);
    static Cell I(string col, long v) => new(col, 'i', null, 0, v);
    static Cell None(string col) => new(col, 'a', null, 0, 0);
    /// <summary>The row last written for each record that a later record with the same id revises (kind and id). The revision retracts that row and writes its own, so the rows of the source are a function of the records: two rows of one id would be two fragments with different values for one element (Def. 6.4), not a revision.</summary>
    readonly Dictionary<string, Cell[]> current = new(StringComparer.Ordinal);

    void Put(IRowSink sink, string rowType, string? revises, params Cell[] cells)
    {
        void Write(long weight, Cell[] row)
        {
            var w = sink.Begin(rowType); w.Begin(weight);
            foreach (var c in row)
                switch (c.Kind) { case 't': w.Text(c.Col, c.Text); break; case 'd': w.Dec(w.Col(c.Col), c.Dec); break; case 'i': w.Int(w.Col(c.Col), c.Int); break; default: w.Absent(w.Col(c.Col)); break; }
        }
        if (revises is not null && current.TryGetValue(revises, out var old)) Write(-1, old);
        Write(1, cells);
        if (revises is not null) current[revises] = cells;
    }

    public void Consume(Envelope e, IRowSink sink)
    {
        var kind = e.Payload.S("kind"); var cap = e.Payload.S("capId") ?? e.Payload.S("id");
        if (cap is null) { sink.Rejected("authoring", "row without capId or id"); return; }
        if (kind == "capability")
        {
            var name = e.Payload.S("name"); var owner = e.Payload.S("owner"); var status = e.Payload.S("status");
            if (name is null || owner is null || status is null) { sink.Rejected("authoring", "capability row without name, owner or status"); return; }
            Put(sink, "CapabilityRow", "capability/" + cap, T("capId", cap), T("name", name), T("owner", owner), T("status", status), T("parentId", J.NullIfEmpty(e.Payload.S("parentId"))));
        }
        else if (kind == "realizes")
        {
            var cluster = e.Payload.S("cluster"); var ns = e.Payload.S("namespace"); var svc = e.Payload.S("service");
            if (cluster is null || ns is null || svc is null) { sink.Rejected("authoring", "realizes row without cluster, namespace or service"); return; }
            Put(sink, "RealizesRow", null, T("capId", cap), T("cluster", cluster), T("namespace", ns), T("service", svc));
        }
        else if (kind == "signalRule")
        {
            var type = e.Payload.S("type"); var meter = e.Payload.S("meter");
            if (type is null || meter is null || e.Payload.P("above") is not { } ab || e.Payload.P("below") is not { } be || e.Payload.P("enterAfter") is not { } en || e.Payload.P("exitAfter") is not { } ex || e.Payload.P("severity") is not { } sv
                || !ab.TryGetDecimal(out var above) || !be.TryGetDecimal(out var below) || !en.TryGetInt64(out var enter) || !ex.TryGetInt64(out var exit) || !sv.TryGetInt64(out var sev) || sev is < 1 or > 2)
            { sink.Rejected("authoring", "signalRule row without type, meter, thresholds or a severity of 1 or 2"); return; }
            if (above <= below || enter < 1 || exit < 1) { sink.Rejected("authoring", "signalRule row whose above is not greater than its below, or with enterAfter or exitAfter below 1"); return; }
            Put(sink, "SignalRuleRow", "signalRule/" + cap, T("ruleId", cap), T("type", type), T("meter", meter), D("above", above), I("enterAfter", enter), D("below", below), I("exitAfter", exit), I("severity", sev),
                I("invert", e.Payload.P("invert") is { ValueKind: System.Text.Json.JsonValueKind.True } ? 1 : 0));
        }
        else if (kind == "policy")
        {
            var mode = e.Payload.S("mode");
            if (mode is not ("worst" or "share" or "ignore") || (mode == "share" && e.Payload.P("share") is null)) { sink.Rejected("authoring", "policy row with a mode other than worst, share or ignore, or a share policy without a share"); return; }
            Put(sink, "PolicyRow", "policy/" + cap, T("target", cap), T("mode", mode), e.Payload.P("share") is { } sh && sh.TryGetDecimal(out var share) ? D("share", share) : None("share"));
        }
        else if (kind == "curation")
        {
            var action = e.Payload.S("action"); var target = e.Payload.S("target");
            if (action is not ("group" or "hide" or "move" or "critical") || target is null || (action is "group" or "move" && e.Payload.S("value") is null)) { sink.Rejected("authoring", "curation row without a known action, a target, or the value that group and move need"); return; }
            Put(sink, "CurationRow", "curation/" + cap, T("curId", cap), T("action", action), T("target", target), T("value", e.Payload.S("value")));
        }
        else { sink.Rejected("authoring", "row of an unknown kind"); return; }
        Rows++;
    }
}

/// <summary>Activity Log events (management API EventData, camelCase). Newest first and days old: windows of the past, revised as they arrive.</summary>
public sealed class ActivityLogAdapter : ISourceAdapter
{
    public string Id => "azure.activity-log";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("ActivityRow", [RowDef.F("eventId", "Text"), RowDef.F("time", "Instant"), RowDef.F("resourceId", "Text"), RowDef.F("operation", "Text"), RowDef.F("status", "Text"), RowDef.F("caller", "Text", true)]),
    ];
    public bool Handles(string s) => s == "activity-log";
    public long Events { get; private set; }

    /// <summary>The identity of an Activity Log event, the same whichever way it was delivered (management API or diagnostic setting): what happened (operation, resource, status), in which request (correlation id), and when (the event's own time, not its submission or delivery). Correlation, operation and status alone name several events: the same operation of one request is reported again seconds or minutes later.</summary>
    public static string EventId(string? correlation, string operation, string resource, string status, long timeNs) =>
        $"act/{correlation?.ToLowerInvariant()}/{operation.ToLowerInvariant()}/{resource.ToLowerInvariant()}/{status}/{timeNs.ToString(CultureInfo.InvariantCulture)}";
    public void Consume(Envelope e, IRowSink sink)
    {
        var ts = e.Payload.S("eventTimestamp"); var rid = e.Payload.S("resourceId"); var status = e.Payload.PathS("status", "value");
        if (ts is null || rid is null || status is null || !Norm.TryParseInstant(ts, out var ns)) { sink.Rejected("activity-log", "event without timestamp, resourceId or status"); return; }
        var operation = e.Payload.PathS("operationName", "value") ?? "";
        var w = sink.Begin("ActivityRow"); w.Begin();
        w.Text("eventId", EventId(e.Payload.S("correlationId") ?? e.Payload.S("eventDataId") ?? $"{e.File}:{e.Seq}", operation, rid, status, ns)); w.Instant("time", ns); w.Text("resourceId", rid); w.Text("operation", operation); w.Text("status", status); w.Text("caller", J.NullIfEmpty(e.Payload.S("caller")));
        Events++;
    }
}

/// <summary>Azure Monitor platform metrics (az monitor metrics list): one row per datapoint with an average.</summary>
public sealed class AzureMetricsAdapter : ISourceAdapter
{
    public string Id => "azure.metrics";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("ResourceMetricRow", [RowDef.F("time", "Instant"), RowDef.F("resourceId", "Text"), RowDef.F("metric", "Text"), RowDef.F("value", "Decimal")]),
    ];
    public bool Handles(string s) => s == "azure-metrics";
    public long Samples { get; private set; }
    public void Consume(Envelope e, IRowSink sink)
    {
        var rid = e.Meta.S("resourceId");
        if (rid is null || e.Payload.P("value") is not { ValueKind: JsonValueKind.Array } metrics) { sink.Rejected("azure-metrics", "no resourceId or value"); return; }
        foreach (var m in metrics.EnumerateArray())
        {
            var name = m.PathS("name", "value");
            if (name is null || m.P("timeseries") is not { ValueKind: JsonValueKind.Array } series) continue;
            foreach (var s in series.EnumerateArray())
                if (s.P("data") is { ValueKind: JsonValueKind.Array } data)
                    foreach (var d in data.EnumerateArray())
                    {
                        if (d.S("timeStamp") is not { } t || !Norm.TryParseInstant(t, out var ns) || (d.P("average") ?? d.P("total")) is not { ValueKind: JsonValueKind.Number } avg) continue;
                        var w = sink.Begin("ResourceMetricRow"); w.Begin();
                        w.Instant("time", ns); w.Text("resourceId", rid); w.Text("metric", name); w.Dec(w.Col("value"), avg.GetDecimal());
                        Samples++;
                    }
        }
    }
}

/// <summary>VNet flow logs (Network Watcher, format version 2): one row per flow tuple. A record is one blob element; the tuples are comma separated strings of 13 fields (lab/CAPTURE-FORMAT.md).</summary>
public sealed class VnetFlowLogAdapter : ISourceAdapter
{
    public string Id => "azure.vnet-flow-log";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("FlowLogRow", [RowDef.F("time", "Instant"), RowDef.F("vnetId", "Text"), RowDef.F("rule", "Text"), RowDef.F("direction", "Text"), RowDef.F("state", "Text"), RowDef.F("protocol", "Integer"),
            RowDef.F("srcIp", "Text"), RowDef.F("dstIp", "Text"), RowDef.F("srcPort", "Integer"), RowDef.F("dstPort", "Integer"), RowDef.F("packets", "Integer"), RowDef.F("bytes", "Integer"), RowDef.F("packetsBack", "Integer"), RowDef.F("bytesBack", "Integer")]),
    ];
    public bool Handles(string s) => s == "vnet-flow-log";
    public long Tuples { get; private set; }

    public void Consume(Envelope e, IRowSink sink)
    {
        var vnet = e.Payload.S("targetResourceID");
        if (vnet is null || e.Payload.Path("flowRecords", "flows") is not { ValueKind: JsonValueKind.Array } flows) { sink.Rejected("vnet-flow-log", "no targetResourceID or flows"); return; }
        foreach (var flow in flows.EnumerateArray())
        {
            if (flow.P("flowGroups") is not { ValueKind: JsonValueKind.Array } groups) continue;
            foreach (var group in groups.EnumerateArray())
            {
                var rule = group.S("rule") ?? "";
                if (group.P("flowTuples") is not { ValueKind: JsonValueKind.Array } tuples) continue;
                foreach (var t in tuples.EnumerateArray())
                {
                    var f = t.GetString()?.Split(',');
                    if (f is null || f.Length != 13 || !long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)) { sink.Rejected("vnet-flow-log", "malformed tuple"); continue; }
                    var w = sink.Begin("FlowLogRow"); w.Begin();
                    w.Instant("time", ms * 1_000_000L); w.Text("vnetId", vnet); w.Text("rule", rule); w.Text("direction", f[6]); w.Text("state", f[7]);
                    Num(w, "protocol", f[5]); w.Text("srcIp", f[1]); w.Text("dstIp", f[2]); Num(w, "srcPort", f[3]); Num(w, "dstPort", f[4]);
                    Num(w, "packets", f[9]); Num(w, "bytes", f[10]); Num(w, "packetsBack", f[11]); Num(w, "bytesBack", f[12]);
                    Tuples++;
                }
            }
        }
    }

    static void Num(RowWriter w, string col, string v) => w.Int(w.Col(col), long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0);
}

/// <summary>Activity Log records written by the subscription's diagnostic setting to the `sattle-activity` hub: `{records:[...]}` in the diagnostic-settings schema (upper-case resource ids, operationName a string, resultType Start, Success, Failure or Unknown). Same rows as the management API adapter.</summary>
public sealed class DiagnosticActivityAdapter : ISourceAdapter
{
    public string Id => "azure.diagnostic-activity";
    public IReadOnlyList<RowContract> Contracts { get; } = new ActivityLogAdapter().Contracts;
    public bool Handles(string s) => s == "eventhub:sattle-activity";
    public long Events { get; private set; }
    public void Consume(Envelope e, IRowSink sink)
    {
        if (e.Payload.P("records") is not { ValueKind: JsonValueKind.Array } records) { sink.Rejected("eventhub:sattle-activity", "no records array"); return; }
        foreach (var r in records.EnumerateArray())
        {
            var ts = r.S("time"); var rid = r.S("resourceId"); var op = r.S("operationName"); var result = r.S("resultType") ?? "";
            if (ts is null || rid is null || op is null || !Norm.TryParseInstant(ts, out var ns)) { sink.Rejected("eventhub:sattle-activity", "record without time, resourceId or operationName"); continue; }
            var status = result switch { "Start" => "Started", "Success" => "Succeeded", "Failure" => "Failed", "Accept" => "Accepted", "Cancel" => "Canceled", "" => "Unknown", var x => x };      // the management API's words for the same results
            var w = sink.Begin("ActivityRow"); w.Begin();
            w.Text("eventId", ActivityLogAdapter.EventId(r.S("correlationId"), op, rid, status, ns)); w.Instant("time", ns); w.Text("resourceId", rid); w.Text("operation", op);
            w.Text("status", status); w.Text("caller", null);
            Events++;
        }
    }
}

/// <summary>AKS audit records from the cluster diagnostic setting (`sattle-aks` hub): `{records:[...]}`, category kube-audit-admin carries the Kubernetes audit event as a JSON string in properties.log. Other categories (cluster-autoscaler) are not rows. Only completed requests are rows.</summary>
public sealed class AksAuditAdapter : ISourceAdapter
{
    public string Id => "azure.aks-audit";
    public IReadOnlyList<RowContract> Contracts { get; } =
    [
        new("AuditRow", [RowDef.F("eventId", "Text"), RowDef.F("time", "Instant"), RowDef.F("verb", "Text"), RowDef.F("resource", "Text"), RowDef.F("namespace", "Text", true), RowDef.F("name", "Text", true), RowDef.F("user", "Text", true), RowDef.F("code", "Integer", true)]),
    ];
    public bool Handles(string s) => s == "eventhub:sattle-aks";
    public long Events { get; private set; }
    public void Consume(Envelope e, IRowSink sink)
    {
        if (e.Payload.P("records") is not { ValueKind: JsonValueKind.Array } records) { sink.Rejected("eventhub:sattle-aks", "no records array"); return; }
        foreach (var r in records.EnumerateArray())
        {
            if (r.S("category") != "kube-audit-admin") continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(r.PathS("properties", "log") ?? ""); } catch (JsonException) { sink.Rejected("eventhub:sattle-aks", "properties.log is not JSON"); continue; }
            using (doc)
            {
                var a = doc.RootElement;
                if (a.S("stage") != "ResponseComplete") continue;
                var id = a.S("auditID"); var verb = a.S("verb"); var res = a.PathS("objectRef", "resource");
                if (id is null || verb is null || res is null || a.S("requestReceivedTimestamp") is not { } ts || !Norm.TryParseInstant(ts, out var ns)) { sink.Rejected("eventhub:sattle-aks", "audit record without id, verb, resource or time"); continue; }
                var w = sink.Begin("AuditRow"); w.Begin();
                w.Text("eventId", id); w.Instant("time", ns); w.Text("verb", verb); w.Text("resource", res);
                w.Text("namespace", J.NullIfEmpty(a.PathS("objectRef", "namespace"))); w.Text("name", J.NullIfEmpty(a.PathS("objectRef", "name"))); w.Text("user", J.NullIfEmpty(a.PathS("user", "username")));
                if (a.Path("responseStatus", "code") is { ValueKind: JsonValueKind.Number } c) w.Int(w.Col("code"), c.GetInt64()); else w.Absent(w.Col("code"));
                Events++;
            }
        }
    }
}

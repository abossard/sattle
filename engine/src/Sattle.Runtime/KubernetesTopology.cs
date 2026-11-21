using System.Text.Json;

namespace Sattle.Runtime;

/// <summary>Object references from the Kubernetes API. One signed row records each declared controller or Azure provider reference.</summary>
sealed class KubernetesTopology
{
    public const string RowType = "KubernetesLinkRow";
    public static readonly RowContract Contract = new(RowType,
        [RowDef.F("cluster", "Text"), RowDef.F("namespace", "Text"), RowDef.F("kind", "Text"),
            RowDef.F("name", "Text"), RowDef.F("target", "Text", true)]);

    sealed record Link(string Cluster, string Namespace, string Kind, string Name, string? Target);
    readonly Dictionary<string, Link> known = new(StringComparer.Ordinal);

    static string? Owner(JsonElement obj, string kind) =>
        obj.Path("metadata", "ownerReferences") is { ValueKind: JsonValueKind.Array } refs
            ? refs.EnumerateArray().FirstOrDefault(r => r.S("kind") == kind && r.P("controller") is { ValueKind: JsonValueKind.True }).S("name")
            : null;

    static string? AzureHost(JsonElement node)
    {
        const string Scheme = "azure://";
        if (node.PathS("spec", "providerID") is not { } provider || !provider.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var id = provider[Scheme.Length..].ToLowerInvariant();
        if (!id.StartsWith("/subscriptions/", StringComparison.Ordinal)) return null;
        const string Instance = "/virtualmachines/";
        var instance = id.LastIndexOf(Instance, StringComparison.Ordinal);
        return instance > 0 && id.Contains("/virtualmachinescalesets/", StringComparison.Ordinal)
            ? id[..instance]
            : id;
    }

    public void Observe(string key, string cluster, string ns, string kind, string name, JsonElement obj, bool deleted, IRowSink sink)
    {
        if (deleted) { Remove(key, sink); return; }
        var next = kind switch
        {
            "Pod" when Owner(obj, "ReplicaSet") is { } owner => new Link(cluster, ns, "podOwner", name, owner),
            "ReplicaSet" => new Link(cluster, ns, "replicaSet", name, Owner(obj, "Deployment")),
            "Node" when AzureHost(obj) is { } host => new Link(cluster, ns, "azureHost", name, host),
            "Namespace" => new Link(cluster, ns, "namespace", name, null),
            _ => null,
        };
        if (known.GetValueOrDefault(key) == next) return;
        Remove(key, sink);
        if (next is not null)
        {
            known[key] = next;
            Write(sink, next, 1);
        }
    }

    public void Remove(string key, IRowSink sink)
    {
        if (known.Remove(key, out var old)) Write(sink, old, -1);
    }

    public IEnumerable<string> Objects(string kind) => known.Where(p => p.Value.Kind == kind).Select(p => p.Key);

    static void Write(IRowSink sink, Link link, long weight)
    {
        var w = sink.Begin(RowType);
        w.Begin(weight);
        w.Text("cluster", link.Cluster);
        w.Text("namespace", link.Namespace);
        w.Text("kind", link.Kind);
        w.Text("name", link.Name);
        w.Text("target", link.Target);
    }
}

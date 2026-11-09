using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;
using Sattle.Projections;
using Sattle.Runtime;
namespace Sattle.Adapters;

public enum SourceFamily { Hubble, AzureResourceGraph, Kubernetes, AzureNetworkWatcher, OpenTelemetry }
public sealed record AdapterDefinition(SourceFamily Family, string Version,
    ImmutableArray<NativeFormat> Formats, ProjectionDefinition Mapping);
// One contract for file, event bus, batches and gRPC-produced records. No client SDK dependency.
public interface IGraphAdapter<TNative>
{
    IAsyncEnumerable<AdaptationResult> AdaptAsync(IAsyncEnumerable<NativeInput<TNative>> input,
        AdapterDefinition definition, CancellationToken cancellationToken = default);
}
public sealed record NativeInput<T>(InputContext Context, T Payload);
public sealed record IdentityCandidate(string LocalName, IdentityKey Key);
public sealed record RelationshipCandidate(string LocalName, string SourceLocalName,
    string TargetLocalName, TypeId Type, ImmutableArray<PropertyValue> Properties);
public sealed record EntityCandidate(string LocalName, IdentityKey Identity, TypeId Type,
    ImmutableArray<PropertyValue> Properties);
public sealed record GraphObservation(string ContributionKey, ImmutableArray<EntityCandidate> Entities,
    ImmutableArray<RelationshipCandidate> Relationships, ImmutableArray<IdentityEvidence> Evidence);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AdaptedObservation), "observation")]
[JsonDerivedType(typeof(AdaptedScopeEnd), "scopeEnd")]
[JsonDerivedType(typeof(AdaptedLoss), "loss")]
[JsonDerivedType(typeof(AdaptedWithdrawal), "withdrawal")]
[JsonDerivedType(typeof(AdaptationRejected), "rejected")]
public abstract record AdaptationResult(TenantId Tenant, InputId Input);
public sealed record AdaptedObservation(TenantId Tenant, InputId Input, GraphObservation Observation) : AdaptationResult(Tenant, Input);
public sealed record AdaptedScopeEnd(TenantId Tenant, InputId Input, SnapshotScope Scope, string ScanId,
    bool AllPagesReceived) : AdaptationResult(Tenant, Input);
public sealed record AdaptedLoss(TenantId Tenant, InputId Input, SourcePosition? Position, string Reason) : AdaptationResult(Tenant, Input);
public sealed record AdaptationRejected(TenantId Tenant, InputId Input, ImmutableArray<Diagnostic> Errors) : AdaptationResult(Tenant, Input);

// Transport carriers preserve native bytes. Actual vendor schemas remain authoritative.
public sealed record HubbleMessage(NativeFormat Format, ImmutableArray<byte> Bytes);
public sealed record AzureResourceGraphPage(NativeFormat Format, ImmutableArray<byte> Bytes,
    string ScanId, SnapshotScope Scope, string? SkipToken, bool ResultTruncated);
public sealed record KubernetesMessage(NativeFormat Format, ImmutableArray<byte> Bytes,
    string ClusterIdentity, string? ResourceVersion);
public sealed record NetworkWatcherRecord(NativeFormat Format, ImmutableArray<byte> Bytes,
    string FlowLogResourceId, int FlowLogVersion);
public sealed record OpenTelemetryExport(NativeFormat Format, ImmutableArray<byte> Bytes,
    string Signal, string? SchemaUrl);
public interface IHubbleAdapter : IGraphAdapter<HubbleMessage>;
public interface IAzureResourceGraphAdapter : IGraphAdapter<AzureResourceGraphPage>;
public interface IKubernetesAdapter : IGraphAdapter<KubernetesMessage>;
public interface INetworkWatcherAdapter : IGraphAdapter<NetworkWatcherRecord>;
public interface IOpenTelemetryAdapter : IGraphAdapter<OpenTelemetryExport>;

public sealed record AdaptedWithdrawal(TenantId Tenant, InputId Input, string Source, string ContributionKey) : AdaptationResult(Tenant, Input);

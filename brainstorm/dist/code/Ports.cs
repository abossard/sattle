using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;
using Sattle.Projections;
namespace Sattle.Runtime;

public sealed record SourcePosition(string Source, string Partition, string Position);
public sealed record InputContext(TenantId Tenant, string Source, InputId Delivery,
    SourcePosition? Position, string? ObservedAt, string? ReceivedAt);
public enum Coverage { ObservationOnly, CompleteScope }
public sealed record SnapshotScope(string QueryDigest, ImmutableArray<string> Boundaries,
    Coverage Coverage, bool PointInTimeConsistent);
public sealed record NativeFormat(string Family, string MessageType, string SchemaVersion, string MediaType);
// The source-native payload is not rewritten. Context is sidecar metadata, not mandatory payload fields.
public sealed record NativeRecord(InputContext Context, NativeFormat Format, ImmutableArray<byte> Payload);
public sealed record IngestBatch(RequestId Request, ImmutableArray<NativeRecord> Records);
public sealed record IngestionReceipt(RequestId Request, InputId Delivery, long AcceptedSequence,
    string DurableCheckpoint);
public sealed record InputFrontier(ImmutableArray<SourcePosition> Positions,
    ImmutableArray<SnapshotBinding> GraphInputs, string ConfigurationVersion);
public sealed record ProjectionCommit(GraphPatch Patch, InputFrontier Frontier);
public sealed record GraphScope(ImmutableArray<EntityId> Roots, int MaximumDepth,
    ImmutableArray<TypeId> RelationshipTypes);
public sealed record WorkBudget(long MaximumFacts, long MaximumSteps);
public sealed record ReadGraph(RequestId Request, GraphVersion Version, GraphScope Scope,
    WorkBudget Budget, string? Continuation);
public enum ReadCompletion { Complete, MoreAvailable, BudgetExhausted, Unavailable }
public sealed record GraphReadPage(RequestId Request, GraphVersion Version, GraphExtent Extent,
    ReadCompletion Completion, string? Continuation);
public sealed record SubscribeGraph(RequestId Request, TenantId Tenant, GraphId Graph, GraphScope Scope,
    DiffProfile Profile, WorkBudget Budget);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SubscriptionPage), "page")]
[JsonDerivedType(typeof(SubscriptionReady), "ready")]
[JsonDerivedType(typeof(SubscriptionChange), "change")]
[JsonDerivedType(typeof(SubscriptionGap), "gap")]
[JsonDerivedType(typeof(SubscriptionProgress), "progress")]
public abstract record GraphFeedItem(RequestId Request);
public sealed record SubscriptionPage(RequestId Request, SnapshotPage Page) : GraphFeedItem(Request);
public sealed record SubscriptionReady(RequestId Request, SnapshotSeal Seal,
    InputFrontier Frontier, string ResumeToken) : GraphFeedItem(Request);
public sealed record SubscriptionChange(RequestId Request, ProjectionCommit Commit,
    string ResumeToken) : GraphFeedItem(Request);
public sealed record SubscriptionProgress(RequestId Request, InputFrontier Frontier) : GraphFeedItem(Request);
public sealed record SubscriptionGap(RequestId Request, string Reason,
    GraphVersion LastAvailable) : GraphFeedItem(Request);
public interface IGraphSpace
{
    IAsyncEnumerable<GraphReadPage> ReadAsync(IAsyncEnumerable<ReadGraph> queries,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<GraphFeedItem> SubscribeAsync(IAsyncEnumerable<SubscribeGraph> subscriptions,
        CancellationToken cancellationToken = default);
}
public interface IIdentityCatalog
{
    IAsyncEnumerable<IdentityResolution> ResolveAsync(IAsyncEnumerable<ResolveIdentity> requests,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IdentityFound), "found")]
[JsonDerivedType(typeof(IdentityRejected), "rejected")]
public abstract record IdentityResolution;
public sealed record IdentityFound(ResolvedIdentity Value) : IdentityResolution;
public sealed record IdentityRejected(Rejection Error) : IdentityResolution;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InstallOntology), "ontology")]
[JsonDerivedType(typeof(InstallGraphSpace), "graphSpace")]
[JsonDerivedType(typeof(InstallProjection), "projection")]
[JsonDerivedType(typeof(EditProjection), "edit")]
public abstract record ConfigurationCommand(RequestId Request, TenantId Tenant);
public sealed record InstallOntology(RequestId Request, TenantId Tenant, OntologyDefinition Definition) : ConfigurationCommand(Request, Tenant);
public sealed record InstallGraphSpace(RequestId Request, TenantId Tenant, GraphSpaceDefinition Definition) : ConfigurationCommand(Request, Tenant);
public sealed record InstallProjection(RequestId Request, TenantId Tenant, string GraphSpace, GraphProjectionDefinition Definition) : ConfigurationCommand(Request, Tenant);
public sealed record EditProjection(RequestId Request, TenantId Tenant, ProjectionId Projection, string ExpectedVersion,
    ProjectionDefinition Replacement) : ConfigurationCommand(Request, Tenant);
public sealed record ConfigurationReceipt(RequestId Request, bool Accepted,
    string? InstalledVersion, ImmutableArray<Diagnostic> Errors);
public interface IProjectionEngine
{
    IAsyncEnumerable<IngestResult> IngestAsync(IAsyncEnumerable<EngineInput> input,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<ConfigurationReceipt> ConfigureAsync(IAsyncEnumerable<ConfigurationCommand> commands,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<TelemetryObservation> ObserveTelemetryAsync(IAsyncEnumerable<TelemetrySubscription> projections,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InputAccepted), "accepted")]
[JsonDerivedType(typeof(InputRejected), "rejected")]
public abstract record IngestResult;
public sealed record InputAccepted(IngestionReceipt Receipt) : IngestResult;
public sealed record InputRejected(RequestId Request, InputId Delivery,
    ImmutableArray<Diagnostic> Errors) : IngestResult;

public sealed record EvaluationInput(InputId Input, long Sequence, ImmutableArray<GraphVersion> Graphs,
    ImmutableArray<GraphPatch> Patches, GraphExtent AdaptedFacts, long LogicalTimeNanoseconds);
public sealed record EvaluationRequest(RequestId Request, CompiledProjection Projection,
    GraphVersion Previous, InputFrontier Frontier, EvaluationInput Input);
public sealed record EvaluationProposal(RequestId Request, GraphPatch? Change,
    ImmutableArray<TelemetryObservation> Telemetry, ImmutableArray<ElementHistory> HistoryChanges);
// No runtime is supplied. Commit owns revision checks and atomic identity/history/correspondence updates.
public interface IProjectionEvaluator
{
    IAsyncEnumerable<EvaluationResult> EvaluateAsync(IAsyncEnumerable<EvaluationRequest> input,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EvaluationSucceeded), "succeeded")]
[JsonDerivedType(typeof(EvaluationFailed), "failed")]
public abstract record EvaluationResult;
public sealed record EvaluationSucceeded(EvaluationProposal Proposal) : EvaluationResult;
public sealed record EvaluationFailed(Rejection Error) : EvaluationResult;
public sealed record LensContext(string RuleVersion, Revision ComplementRevision,
    ImmutableArray<Correspondence> Complement);
public sealed record LensRequest(BackwardEdit Edit, LensContext Context);
public interface IBidirectionalProjection
{
    IAsyncEnumerable<LensResult> ProposeBackwardAsync(IAsyncEnumerable<LensRequest> edits,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LensProposed), "proposed")]
[JsonDerivedType(typeof(LensRejected), "rejected")]
public abstract record LensResult;
public sealed record LensProposed(BackwardProposal Proposal) : LensResult;
public sealed record LensRejected(Rejection Error) : LensResult;

public sealed record TelemetrySubscription(TenantId Tenant, ProjectionId Projection);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NativeBatchInput), "native")]
[JsonDerivedType(typeof(GraphSnapshotInput), "snapshotPage")]
[JsonDerivedType(typeof(GraphSnapshotComplete), "snapshotComplete")]
[JsonDerivedType(typeof(GraphChangeInput), "graphChange")]
[JsonDerivedType(typeof(LogicalTimeInput), "logicalTime")]
public abstract record EngineInput;
public sealed record NativeBatchInput(IngestBatch Batch) : EngineInput;
public sealed record GraphSnapshotInput(RequestId Request, InputContext Context, string Binding,
    SnapshotPage Page) : EngineInput;
public sealed record GraphSnapshotComplete(RequestId Request, InputContext Context, string Binding,
    SnapshotSeal Seal, InputFrontier Frontier) : EngineInput;
public sealed record GraphChangeInput(RequestId Request, InputContext Context, string Binding,
    GraphPatch Patch, InputFrontier Frontier) : EngineInput;
public sealed record LogicalTimeInput(RequestId Request, InputContext Context, long Nanoseconds) : EngineInput;

public enum VirtualQueryKind { IdentityLookup, BoundedNeighborhood, FiniteExtent }
public sealed record VirtualProviderContract(ContentHash Artifact, string Version,
    ContentHash DefinitionSchemaDigest, ImmutableArray<VirtualQueryKind> Queries);
public sealed record ReadVirtual(ReadGraph Query, VirtualRegion Region, VirtualProviderContract Provider);
public interface IVirtualGraphProvider
{
    IAsyncEnumerable<GraphReadPage> ReadAsync(IAsyncEnumerable<ReadVirtual> queries,
        CancellationToken cancellationToken = default);
}

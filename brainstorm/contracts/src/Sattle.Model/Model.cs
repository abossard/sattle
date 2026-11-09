using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Sattle.Model;

// DTOs are untrusted proposals until schema + semantic validation succeeds.
// Value wrappers prevent accidental mixing, not forged IDs or default values.
public readonly record struct TenantId(string Value);
public readonly record struct EntityId(string Value);
public readonly record struct RelationshipId(string Value);
public readonly record struct ProjectionId(string Value);
public readonly record struct GraphId(string Value);
public readonly record struct InputId(string Value);
public readonly record struct TypeId(string Value);
public readonly record struct PropertyId(string Value);
public readonly record struct OperatorId(string Value);
public readonly record struct DocumentId(string Value);
public readonly record struct BlockId(string Value);
public readonly record struct Revision(string Value);
public readonly record struct RequestId(string Value);
public readonly record struct ContentHash(string Value);
public readonly record struct IdentityDomain(string Value);
public sealed record OntologyRef(string Id, string Version, ContentHash Digest);
public sealed record GraphVersion(TenantId Tenant, GraphId Graph, Revision Revision);
public sealed record Diagnostic(string Code, string Path, string Message);
public sealed record Correlated<T>(RequestId Request, T Value);
public sealed record Rejection(RequestId Request, ImmutableArray<Diagnostic> Errors);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextValue), "text")]
[JsonDerivedType(typeof(IntegerValue), "integer")]
[JsonDerivedType(typeof(DecimalValue), "decimal")]
[JsonDerivedType(typeof(BooleanValue), "boolean")]
[JsonDerivedType(typeof(InstantValue), "instant")]
[JsonDerivedType(typeof(NullValue), "null")]
[JsonDerivedType(typeof(EntityValue), "entity")]
[JsonDerivedType(typeof(ListValue), "list")]
public abstract record GraphValue;
public sealed record TextValue(string Value) : GraphValue;
// Canonical decimal strings avoid JSON precision loss. Validation enforces grammar.
public sealed record IntegerValue(string Value) : GraphValue;
public sealed record DecimalValue(string Value) : GraphValue;
public sealed record BooleanValue(bool Value) : GraphValue;
public sealed record InstantValue(string Value) : GraphValue;
public sealed record NullValue : GraphValue;
public sealed record EntityValue(EntityId Value) : GraphValue;
public sealed record ListValue(ImmutableArray<GraphValue> Items) : GraphValue;
public sealed record PropertyValue(PropertyId Property, GraphValue Value);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityRef), "entity")]
[JsonDerivedType(typeof(RelationshipRef), "relationship")]
public abstract record ElementRef;
public sealed record EntityRef(EntityId Id) : ElementRef;
public sealed record RelationshipRef(ProjectionId Projection, RelationshipId Id) : ElementRef;

public sealed record EntityState(EntityId Id, TypeId Type, ImmutableArray<PropertyValue> Properties);
// Type is NOT part of immutable edge identity. Unique (source,target,type) is an index.
public sealed record RelationshipState(RelationshipId Id, EntityId Source, EntityId Target,
    TypeId Type, ImmutableArray<PropertyValue> Properties);
public sealed record GraphExtent(ImmutableArray<EntityState> Entities,
    ImmutableArray<RelationshipState> Relationships);
public sealed record SnapshotBinding(string Port, GraphVersion Snapshot);
public sealed record VirtualRegion(string Id, ContentHash ProviderDigest, string ProviderVersion,
    string Definition, ImmutableArray<SnapshotBinding> Inputs);
public sealed record GraphDescriptor(GraphVersion Version, ProjectionId Projection,
    OntologyRef Ontology, ImmutableArray<VirtualRegion> VirtualRegions);
// A page is not a graph snapshot by itself; seal binds all pages and virtual regions.
public sealed record SnapshotPage(string SnapshotToken, long Page, GraphExtent Extent);
public sealed record SnapshotSeal(string SnapshotToken, GraphDescriptor Descriptor,
    long PageCount, ContentHash Digest);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityEdit), "entity")]
[JsonDerivedType(typeof(RelationshipEdit), "relationship")]
[JsonDerivedType(typeof(VirtualRegionEdit), "virtualRegion")]
[JsonDerivedType(typeof(OntologyEdit), "ontology")]
public abstract record GraphEdit;
// null state = absence; NullValue = present property whose value is null.
public sealed record EntityEdit(EntityId Id, EntityState? Before, EntityState? After) : GraphEdit;
public sealed record RelationshipEdit(RelationshipId Id, RelationshipState? Before,
    RelationshipState? After) : GraphEdit;
public sealed record VirtualRegionEdit(string Id, VirtualRegion? Before, VirtualRegion? After) : GraphEdit;
public sealed record OntologyEdit(OntologyRef Before, OntologyRef After) : GraphEdit;
public enum DiffProfile { ExtensionalFinite, Symbolic }
public sealed record ChangeCause(ImmutableArray<InputId> Inputs, string ConfigurationVersion);
public sealed record GraphPatch(string SchemaVersion, GraphVersion From, GraphVersion To,
    ProjectionId Projection, DiffProfile Profile, ImmutableArray<GraphEdit> Edits, ChangeCause Cause);
public sealed record PatchBatch(ImmutableArray<GraphPatch> Patches);
// Batches preserve event revision boundaries. Squashing is separate and lossy.
public sealed record SquashRequest(GraphVersion Base, ImmutableArray<GraphPatch> Patches);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EntityFact), "entity")]
[JsonDerivedType(typeof(RelationshipFact), "relationship")]
public abstract record GraphFact;
public sealed record EntityFact(EntityState State) : GraphFact;
public sealed record RelationshipFact(RelationshipState State) : GraphFact;
public sealed record WeightedFact(GraphFact Fact, string Weight);
public sealed record SignedDelta(string SchemaVersion, TenantId Tenant, ProjectionId Projection,
    OntologyRef Ontology, ImmutableArray<WeightedFact> Terms);
public sealed record DeltaAtBase(SignedDelta Delta, GraphVersion Base, ChangeCause Cause);
public sealed record LossReport(bool ChronologyLost, bool IntermediateStatesLost, bool CauseDetailLost);
public sealed record DeltaConversion(SignedDelta Delta, LossReport Loss);
public enum ValueKind { Text, Integer, Decimal, Boolean, Instant, Entity, List }
public enum ElementKind { Entity, Relationship }
public enum PropertyRole { GraphData, Telemetry }
public sealed record PropertyDefinition(PropertyId Id, ValueKind ValueKind, bool Nullable,
    ValueKind? ItemKind, string? Unit, PropertyRole Role);
public sealed record EntityTypeDefinition(TypeId Id, ImmutableArray<PropertyId> Properties);
public sealed record RelationshipTypeDefinition(TypeId Id, ImmutableArray<TypeId> SourceTypes,
    ImmutableArray<TypeId> TargetTypes, ImmutableArray<PropertyId> Properties);
public sealed record OntologyDefinition(string SchemaVersion, OntologyRef Reference,
    TypeId NoEntityType, TypeId NoRelationshipType, ImmutableArray<PropertyDefinition> Properties,
    ImmutableArray<EntityTypeDefinition> EntityTypes,
    ImmutableArray<RelationshipTypeDefinition> RelationshipTypes);

public sealed record IdentityComponent(string Name, GraphValue Value);
public sealed record IdentityKey(TenantId Tenant, IdentityDomain Domain,
    string CanonicalizationVersion, ImmutableArray<IdentityComponent> Components);
public sealed record IdentityEntry(IdentityKey Key, EntityId Entity);
// Evidence supports possible correlation, not an unconditional union of identities.
public sealed record IdentityEvidence(IdentityKey Left, IdentityKey Right, string Rule,
    ImmutableArray<InputId> Inputs, decimal Confidence);
public sealed record ResolveIdentity(RequestId Request, IdentityKey Key);
public sealed record ResolvedIdentity(RequestId Request, EntityId Entity);
public sealed record ElementHistory(ProjectionId Projection, ElementRef Element,
    ImmutableArray<InputId> InputsInIngestionOrder);
public sealed record RetainedInput(InputId Id, long IngestionSequence, string MediaType,
    ImmutableArray<byte> OriginalBytes);
public sealed record TelemetryObservation(TenantId Tenant, ProjectionId Projection, ElementRef Element,
    PropertyId Property, GraphValue Value, InputId Input);

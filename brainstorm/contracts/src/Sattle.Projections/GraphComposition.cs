using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;

namespace Sattle.Projections;

// Composition of projection configurations. One logical root per graph space;
// tenant scope is supplied by its configuration command. Storage can be distributed.
public sealed record GraphSpaceDefinition(string SchemaVersion, string Id,
    MasterGraphDefinition Master, ImmutableArray<GraphProjectionDefinition> Projections);
public sealed record MasterGraphDefinition(GraphId Graph, ProjectionDefinition IngestionProjection);
public sealed record GraphProjectionDefinition(GraphId OutputGraph, ProjectionDefinition Projection,
    ImmutableArray<ProjectionGraphInput> GraphInputs);
public sealed record ProjectionGraphInput(string Port, GraphSource Source, OntologyRef ExpectedOntology);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MasterGraphSource), "master")]
[JsonDerivedType(typeof(ProjectionGraphSource), "projection")]
public abstract record GraphSource;
public sealed record MasterGraphSource : GraphSource;
public sealed record ProjectionGraphSource(ProjectionId Projection) : GraphSource;

public sealed record CompileGraphSpace(RequestId Request, TenantId Tenant, GraphSpaceDefinition Definition);
public sealed record CompiledGraphSpace(RequestId Request, TenantId Tenant, string GraphSpace,
    ContentHash DefinitionDigest, GraphId MasterGraph, ImmutableArray<CompiledProjection> Projections);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GraphSpaceAccepted), "accepted")]
[JsonDerivedType(typeof(GraphSpaceRejected), "rejected")]
public abstract record GraphSpaceCompilation;
public sealed record GraphSpaceAccepted(CompiledGraphSpace GraphSpace) : GraphSpaceCompilation;
public sealed record GraphSpaceRejected(RequestId Request, ImmutableArray<Diagnostic> Errors) : GraphSpaceCompilation;

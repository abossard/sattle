using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Sattle.Adapters;
using Sattle.Documents;
using Sattle.Model;
using Sattle.Projections;
using Sattle.Runtime;

namespace Sattle.Examples;

// Compile-checked caller examples. Implementations of the ports are supplied by
// the caller; these examples do not implement or simulate the Sattle engine.
public static class UsageExamples
{
    #region composition
    public static GraphSpaceDefinition Compose(
        ProjectionDefinition ingestion, ProjectionDefinition services,
        ProjectionDefinition business)
    {
        // services declares a "master" input; business declares "services"
        // and "inventory" inputs. Their operators consume those exact names.
        return new("1.0", "operations",
            new MasterGraphDefinition(new GraphId("master"), ingestion),
            [
                new(new GraphId("services"), services,
                    [new("master", new MasterGraphSource(), ingestion.Ontology)]),
                new(new GraphId("business"), business,
                    [new("services", new ProjectionGraphSource(services.Id), services.Ontology),
                     new("inventory", new MasterGraphSource(), ingestion.Ontology)])
            ]);
        // Each definition pins its own output ontology. The graph-space
        // compiler still validates the named bindings, operators and types.
    }
    #endregion

    #region subscription
    public static async Task WatchGraphAsync(
        IGraphSpace graphs, SubscribeGraph request,
        Func<GraphFeedItem, CancellationToken, ValueTask> handle,
        CancellationToken ct)
    {
        await foreach (var item in graphs.SubscribeAsync(One(request, ct), ct))
        {
            // The handler distinguishes snapshot pages, the ready seal,
            // committed changes, progress and gaps. A gap requires recovery.
            await handle(item, ct);
        }
        // Graph subscription reconnect/resume is still an open contract item.
    }
    #endregion

    #region ingestion
    public static async Task ConsumeInputsAsync(
        IProjectionEngine engine, IAsyncEnumerable<EngineInput> input,
        Func<IngestResult, CancellationToken, ValueTask> handleReceipt,
        CancellationToken ct)
    {
        // input may yield NativeBatchInput with one record or many records.
        // A file reader or event transport supplies the same async contract.
        await foreach (var receipt in engine.IngestAsync(input, ct).WithCancellation(ct))
            await handleReceipt(receipt, ct); // Handle acceptance AND rejection.
        // Acceptance does not mean that every downstream projection committed.
        // Observe committed graph changes through IGraphSpace.SubscribeAsync.
    }
    #endregion

    #region adapter
    public static IAsyncEnumerable<AdaptationResult> AdaptHubble(
        IHubbleAdapter adapter, IAsyncEnumerable<NativeInput<HubbleMessage>> flows,
        AdapterDefinition mapping, CancellationToken ct)
    {
        // The definition pins the supported native format and mapping version.
        // Results can be observations, withdrawals, loss notices or rejections.
        return adapter.AdaptAsync(flows, mapping, ct);
    }
    #endregion

    #region graph-data
    public static RelationshipState ObservedCall(
        RelationshipId edge, EntityId api, EntityId database) =>
        new(edge, api, database, new TypeId("Calls"),
            [new(new PropertyId("health"), new TextValue("degraded"))]);
    // The output ontology must declare Calls and its text-valued health property.
    // The relationship's identity belongs to its projection, not its current type.
    #endregion

    #region identity
    public static ResolveIdentity PodIdentity(TenantId tenant, string cluster, string uid) =>
        new(new RequestId("resolve-pod"),
            new IdentityKey(tenant, new IdentityDomain("kubernetes-object"), "1",
                [new("cluster", new TextValue(cluster)),
                 new("uid", new TextValue(uid))]));
    // Submit this request to IIdentityCatalog.ResolveAsync. A display name is
    // deliberately absent from the key; renaming it need not change identity.
    #endregion

    #region pattern
    public static MatchOperator FindCalls() =>
        new(new OperatorId("calls"), "master",
            new GraphPattern(
                [new("api", new TypeId("Service")),
                 new("db", new TypeId("Service"))],
                [new("call", "api", "db", new TypeId("Calls"))]));
    // This operator binds rows. A later emit operator produces output facts.
    // Service and Calls are resolved against the "master" input's ontology.
    #endregion

    #region ontology
    public static PropertyDefinition HealthProperty() =>
        new(new PropertyId("health"), ValueKind.Text,
            Nullable: false, ItemKind: null, Unit: null,
            Role: PropertyRole.GraphData);
    // Add the property's ID to the relevant entity or relationship type in
    // OntologyDefinition. A graph pins the ontology's version and digest.
    #endregion

    #region compile
    public static IAsyncEnumerable<GraphSpaceCompilation> ValidateComposition(
        IProjectionCompiler compiler, TenantId tenant, GraphSpaceDefinition space,
        CancellationToken ct) =>
        compiler.CompileGraphSpaceAsync(
            One(new CompileGraphSpace(new RequestId("compile-space"), tenant, space), ct), ct);
    // Inspect GraphSpaceAccepted or GraphSpaceRejected and its diagnostics.
    // Compilation does not install the definition or execute the projections.
    #endregion

    #region union
    public sealed record EndpointPair(EntityId Source, EntityId Target);

    public static Rows<EndpointPair> CombinePairs(
        Rows<EndpointPair> observed, Rows<EndpointPair> expected) =>
        new UnionRows<EndpointPair>(observed, expected);
    // Both inputs have already selected endpoints into the same row shape.
    // This is an authoring expression. Lowering and semantic validation are
    // still required; it does not execute a union or select a conflict policy.
    #endregion

    #region health
    public static GroupOperator AnyFailedDependency() =>
        new(new OperatorId("service-health"),
            [new("service", new FieldExpression("service"))],
            [new("degraded", AggregateFunction.Any,
                new CallExpression(Function.Equal,
                    [new FieldExpression("dependencyHealth"),
                     new Literal(new TextValue("failed"))]))]);
    // Upstream rows supply service and dependencyHealth. A downstream emitter
    // maps the Boolean aggregate into a property declared by its output ontology.
    #endregion

    #region patches
    public static PatchBatch PreserveEventBoundaries(GraphPatch first, GraphPatch next) =>
        new([first, next]);

    public static EvaluatePatch ComposePatches(GraphPatch first, GraphPatch next) =>
        new(new RequestId("compose"),
            new PatchThen(new PatchLiteral(first), new PatchLiteral(next)));
    // Composition requires first.To == next.From and compatible graph scope.
    // The algebra validates before-states; construction alone does not apply edits.
    #endregion

    #region deltas
    public static EvaluateDelta Subtract(SignedDelta left, SignedDelta right) =>
        new(new RequestId("subtract"),
            new DeltaSubtract(new DeltaLiteral(left), new DeltaLiteral(right)));

    public static ConvertDeltaToPatch AtBase(
        SignedDelta delta, GraphVersion basis, GraphVersion target, ChangeCause cause) =>
        new(new RequestId("materialize"), new DeltaAtBase(delta, basis, cause), target);
    // Matching tenant, projection and ontology are required. Applying a signed
    // result also needs a base graph: negative fact counts can be rejected.
    #endregion

    #region bounded-read
    public static ReadGraph Neighborhood(GraphVersion version, EntityId root) =>
        new(new RequestId("neighborhood"), version,
            new GraphScope([root], 2, [new TypeId("Calls")]),
            new WorkBudget(MaximumFacts: 10_000, MaximumSteps: 100_000),
            Continuation: null);
    // Submit through IGraphSpace.ReadAsync for stored or virtual graphs.
    // Inspect Complete, MoreAvailable, BudgetExhausted and Unavailable on each
    // returned page. A page by itself does not imply a complete neighborhood.
    #endregion

    #region recursion
    public static TraverseOperator BoundedDependencies() =>
        new(new OperatorId("dependencies"), new TypeId("Calls"),
            Direction.Outgoing, MinimumDepth: 1, MaximumDepth: 4);
    // This is a bounded traversal. Unbounded fixed-point derivation instead uses
    // FixpointOperator with a finite domain, fact/iteration budgets and a checked
    // monotone body. A budget alone does not establish convergence.
    #endregion

    #region correspondence
    public static Correspondence DeploymentCapability(EntityId deployment, EntityId capability) =>
        new("deployment-to-capability",
            [new EntityRef(deployment)], [new EntityRef(capability)], []);
    // The source deployment and target capability keep distinct identities.
    // This correspondence is one TGG ingredient; it is not a full grammar or
    // evidence that a forward/backward rule pair satisfies the lens laws.
    #endregion

    #region lens
    public static IAsyncEnumerable<LensResult> ProposeBackward(
        IBidirectionalProjection lens, BackwardEdit edit, LensContext context,
        CancellationToken ct) =>
        lens.ProposeBackwardAsync(One(new LensRequest(edit, context), ct), ct);
    // The edit names an explicitly bidirectional rule and source/view base
    // revisions. A proposed source patch still needs validation and atomic commit.
    // This call never writes to Kubernetes or another external source system.
    #endregion

    #region configuration
    public static EditProjection ReplaceDefinition(
        TenantId tenant, ProjectionDefinition replacement, string expectedVersion) =>
        new(new RequestId("edit-projection"), tenant, replacement.Id,
            expectedVersion, replacement);
    // Send through IProjectionEngine.ConfigureAsync and inspect its receipt.
    // The engine owns replay of retained inputs and resulting graph commits.
    #endregion

    #region document
    public static async Task RenderMarkdownAsync(
        IDocumentProjection projection, IDocumentRenderer renderer,
        ProjectDocument request,
        Func<RenderedDocument, CancellationToken, ValueTask> save,
        Func<DocumentResult, CancellationToken, ValueTask> reportFailure,
        CancellationToken ct)
    {
        await foreach (var result in projection.ProjectAsync(One(request, ct), ct))
        {
            if (result.Snapshot is not { } snapshot || !result.Errors.IsEmpty)
            {
                await reportFailure(result, ct);
                continue;
            }
            var render = new RenderDocument(result.Request, snapshot,
                DocumentFormat.Markdown, RendererVersion: "1");
            await foreach (var output in renderer.RenderAsync(One(render, ct), ct))
                await save(output, ct);
        }
        // Snapshot.Sources records the graph versions used. Inspect completeness:
        // explicitly truncated documents remain distinguishable from complete ones.
    }
    #endregion

    private static async IAsyncEnumerable<T> One<T>(T item,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return item;
    }
}

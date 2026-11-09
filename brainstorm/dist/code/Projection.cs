using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;

namespace Sattle.Projections;

public enum ScalarType { Text, Integer, Decimal, Boolean, Instant, Entity, Relationship }
public sealed record Field(string Name, ScalarType Type, bool Optional);
public sealed record RowType(ImmutableArray<Field> Fields);
public sealed record Port(string Name, RowType Row);
public sealed record Wire(OperatorId From, string Output, OperatorId To, string Input);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Literal), "literal")]
[JsonDerivedType(typeof(FieldExpression), "field")]
[JsonDerivedType(typeof(CallExpression), "call")]
public abstract record Expression;
public sealed record Literal(GraphValue Value) : Expression;
public sealed record FieldExpression(string Field) : Expression;
public enum Function { Equal, NotEqual, And, Or, Not, Add, Subtract, Multiply, Divide,
    LessThan, GreaterThan, Concat, Coalesce, LowerInvariant }
public sealed record CallExpression(Function Function, ImmutableArray<Expression> Arguments) : Expression;
public sealed record Assignment(string Field, Expression Value);
public sealed record PropertyAssignment(PropertyId Property, Expression Value);
public sealed record IdentityExpression(IdentityDomain Domain, string CanonicalizationVersion,
    ImmutableArray<Assignment> Components);
public enum ConflictPolicy { RequireEqual, PreferFirstPort, PreferLastPort, PreserveEvidence }
public enum AggregateFunction { CountDistinct, Sum, Min, Max, Any, All }
public sealed record AggregateField(string Name, AggregateFunction Function, Expression Value);
public enum Direction { Outgoing, Incoming, Either }
public sealed record NodePattern(string Variable, TypeId Type);
public sealed record EdgePattern(string Variable, string Source, string Target, TypeId Type);
public sealed record GraphPattern(ImmutableArray<NodePattern> Nodes, ImmutableArray<EdgePattern> Edges);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InputOperator), "input")]
[JsonDerivedType(typeof(MatchOperator), "match")]
[JsonDerivedType(typeof(FilterOperator), "filter")]
[JsonDerivedType(typeof(MapOperator), "map")]
[JsonDerivedType(typeof(JoinOperator), "join")]
[JsonDerivedType(typeof(UnionOperator), "union")]
[JsonDerivedType(typeof(DistinctOperator), "distinct")]
[JsonDerivedType(typeof(GroupOperator), "group")]
[JsonDerivedType(typeof(TraverseOperator), "traverse")]
[JsonDerivedType(typeof(EmitEntityOperator), "emitEntity")]
[JsonDerivedType(typeof(EmitRelationshipOperator), "emitRelationship")]
[JsonDerivedType(typeof(FixpointOperator), "fixpoint")]
[JsonDerivedType(typeof(WindowOperator), "window")]
public abstract record Operator(OperatorId Id);
public sealed record InputOperator(OperatorId Id, string Binding, RowType Row) : Operator(Id);
public sealed record MatchOperator(OperatorId Id, string Binding, GraphPattern Pattern) : Operator(Id);
public sealed record FilterOperator(OperatorId Id, Expression Predicate) : Operator(Id);
public sealed record MapOperator(OperatorId Id, ImmutableArray<Assignment> Fields) : Operator(Id);
public sealed record JoinOperator(OperatorId Id, Expression On) : Operator(Id);
public sealed record UnionOperator(OperatorId Id, ConflictPolicy Conflict) : Operator(Id);
public sealed record DistinctOperator(OperatorId Id) : Operator(Id);
public sealed record GroupOperator(OperatorId Id, ImmutableArray<Assignment> Keys,
    ImmutableArray<AggregateField> Aggregates) : Operator(Id);
public sealed record TraverseOperator(OperatorId Id, TypeId Relationship, Direction Direction,
    int MinimumDepth, int MaximumDepth) : Operator(Id);
public sealed record EmitEntityOperator(OperatorId Id, IdentityExpression Identity, TypeId Type,
    ImmutableArray<PropertyAssignment> Properties) : Operator(Id);
public sealed record EmitRelationshipOperator(OperatorId Id, string IdentityRule, Expression Source,
    Expression Target, TypeId Type, ImmutableArray<PropertyAssignment> Properties,
    ConflictPolicy RetypeCollision) : Operator(Id);
// Body owns a recursive relation port. Ordinary outer wires must remain acyclic.
public sealed record FixpointOperator(OperatorId Id, string Relation, RowType Row,
    ImmutableArray<Operator> Body, ImmutableArray<Wire> Wires, OperatorId Result,
    string FiniteDomainBinding, long MaximumFacts, long MaximumIterations) : Operator(Id);
// Logical time advances only by explicit stream controls; no wall-clock graph mutations.
public enum LatePolicy { Ignore, Revise, Reject }
public sealed record WindowOperator(OperatorId Id, string TimestampField,
    long WidthNanoseconds, LatePolicy LatePolicy) : Operator(Id);

public sealed record InputBinding(string Name, string Contract, string Version);
public sealed record ProjectionDefinition(string SchemaVersion, ProjectionId Id, string Version,
    OntologyRef Ontology, ImmutableArray<InputBinding> Inputs, GraphExtent Scaffold,
    ImmutableArray<Operator> Operators, ImmutableArray<Wire> Wires,
    ImmutableArray<OperatorId> Outputs, int RetainedInputsPerElement,
    ImmutableArray<BidirectionalRule> BidirectionalRules);
public sealed record Correspondence(string Rule, ImmutableArray<ElementRef> Source,
    ImmutableArray<ElementRef> Target, ImmutableArray<PropertyValue> Complement);
public enum LensLaw { GetPut, PutGet }
public sealed record RuleProgram(ImmutableArray<Operator> Operators, ImmutableArray<Wire> Wires,
    ImmutableArray<OperatorId> Outputs);
public sealed record CorrespondenceTemplate(string Name, ImmutableArray<string> SourceVariables,
    ImmutableArray<string> TargetVariables, ImmutableArray<Assignment> Complement);
public sealed record BidirectionalRule(string Id, GraphPattern SourcePattern,
    GraphPattern TargetPattern, ImmutableArray<CorrespondenceTemplate> Correspondences,
    RuleProgram Forward, RuleProgram Backward, ImmutableArray<LensLaw> RequiredLaws);
public sealed record BackwardEdit(RequestId Request, GraphVersion SourceBase,
    GraphVersion ViewBase, GraphPatch ViewEdit, string Rule, string RuleVersion);
public sealed record BackwardProposal(RequestId Request, GraphPatch SourceEdit,
    ImmutableArray<Correspondence> UpdatedCorrespondence);

// Typed authoring describes portable expressions, never arbitrary .NET delegates.
public abstract record Expr<T>;
public sealed record Constant<T>(T Value) : Expr<T>;
public sealed record Column<T>(string Name) : Expr<T>;
public sealed record Equal<T>(Expr<T> Left, Expr<T> Right) : Expr<bool>;
public sealed record And(Expr<bool> Left, Expr<bool> Right) : Expr<bool>;
public sealed record Add(Expr<decimal> Left, Expr<decimal> Right) : Expr<decimal>;
public sealed record Property<T>(PropertyId Id);
public sealed record PropertyBinding<T>(Property<T> Property, Expr<T> Value);
public sealed record EntityType<T>(TypeId Id);
public sealed record RelationshipType<TSource, TTarget>(TypeId Id);
public sealed record EntityVariable<T>(string Name);
public sealed record TypedPattern<TSource, TTarget>(EntityVariable<TSource> Source,
    RelationshipType<TSource, TTarget> Relationship, EntityVariable<TTarget> Target);
public sealed record AuthoringRequest<T>(RequestId Request, TenantId Tenant, T Definition);
public sealed record CompiledProjection(TenantId Tenant, ProjectionId Id, string Version, ContentHash DefinitionDigest,
    ImmutableArray<string> RequiredCapabilities);
public interface IProjectionCompiler
{
    IAsyncEnumerable<GraphSpaceCompilation> CompileGraphSpaceAsync(IAsyncEnumerable<CompileGraphSpace> definitions,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<CompilationResult> CompileAsync(IAsyncEnumerable<AuthoringRequest<ProjectionDefinition>> definitions,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CompilationAccepted), "accepted")]
[JsonDerivedType(typeof(CompilationRejected), "rejected")]
public abstract record CompilationResult;
public sealed record CompilationAccepted(RequestId Request, CompiledProjection Projection) : CompilationResult;
public sealed record CompilationRejected(RequestId Request, ImmutableArray<Diagnostic> Errors) : CompilationResult;

// Typed row composition prevents mixing unrelated row streams. Lowering still validates field names.
public abstract record Rows<TRow>;
public sealed record ReadRows<TRow>(string Binding, RowType Contract) : Rows<TRow>;
public sealed record UnionRows<TRow>(Rows<TRow> Left, Rows<TRow> Right) : Rows<TRow>;
public sealed record FilterRows<TRow>(Rows<TRow> Input, RowPredicate<TRow> Predicate) : Rows<TRow>;
public abstract record RowPredicate<TRow>;
public sealed record Member<TRow, TValue>(string Name);
public sealed record RowEqual<TRow, TValue>(Member<TRow, TValue> Member, Expr<TValue> Value) : RowPredicate<TRow>;
public sealed record LowerRows<TRow>(RequestId Request, TenantId Tenant, Rows<TRow> Rows, OntologyRef Ontology);
public sealed record LoweredRows(RequestId Request, ImmutableArray<Operator> Operators,
    ImmutableArray<Wire> Wires, OperatorId Output, ImmutableArray<Diagnostic> Errors);
public interface IProjectionAuthoring
{
    IAsyncEnumerable<LoweredRows> LowerAsync<TRow>(IAsyncEnumerable<LowerRows<TRow>> expressions,
        CancellationToken cancellationToken = default);
}

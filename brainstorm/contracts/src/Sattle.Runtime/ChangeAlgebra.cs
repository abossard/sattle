using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;
namespace Sattle.Runtime;

// Distinct expression trees make mixing ordered and commutative arithmetic a type error.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PatchLiteral), "literal")]
[JsonDerivedType(typeof(PatchThen), "then")]
[JsonDerivedType(typeof(PatchInverse), "inverse")]
public abstract record PatchExpression;
public sealed record PatchLiteral(GraphPatch Patch) : PatchExpression;
public sealed record PatchThen(PatchExpression First, PatchExpression Next) : PatchExpression;
public sealed record PatchInverse(PatchExpression Patch) : PatchExpression;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DeltaLiteral), "literal")]
[JsonDerivedType(typeof(DeltaAdd), "add")]
[JsonDerivedType(typeof(DeltaSubtract), "subtract")]
[JsonDerivedType(typeof(DeltaNegate), "negate")]
public abstract record DeltaExpression;
public sealed record DeltaLiteral(SignedDelta Delta) : DeltaExpression;
public sealed record DeltaAdd(DeltaExpression Left, DeltaExpression Right) : DeltaExpression;
public sealed record DeltaSubtract(DeltaExpression Left, DeltaExpression Right) : DeltaExpression;
public sealed record DeltaNegate(DeltaExpression Delta) : DeltaExpression;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EvaluatePatch), "patch")]
[JsonDerivedType(typeof(EvaluateDelta), "delta")]
[JsonDerivedType(typeof(ConvertPatchToDelta), "toDelta")]
[JsonDerivedType(typeof(ConvertDeltaToPatch), "toPatch")]
[JsonDerivedType(typeof(SquashPatches), "squash")]
public abstract record AlgebraRequest(RequestId Request);
public sealed record SquashPatches(RequestId Request, SquashRequest Squash) : AlgebraRequest(Request);
public sealed record EvaluatePatch(RequestId Request, PatchExpression Expression) : AlgebraRequest(Request);
public sealed record EvaluateDelta(RequestId Request, DeltaExpression Expression) : AlgebraRequest(Request);
public sealed record ConvertPatchToDelta(RequestId Request, GraphPatch Patch, OntologyRef Ontology) : AlgebraRequest(Request);
public sealed record ConvertDeltaToPatch(RequestId Request, DeltaAtBase Delta,
    GraphVersion ProposedTarget) : AlgebraRequest(Request);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PatchResult), "patch")]
[JsonDerivedType(typeof(DeltaResult), "delta")]
[JsonDerivedType(typeof(AlgebraRejected), "rejected")]
[JsonDerivedType(typeof(SquashedPatchResult), "squashed")]
public abstract record AlgebraResult(RequestId Request);
public sealed record PatchResult(RequestId Request, GraphPatch Patch) : AlgebraResult(Request);
public sealed record SquashedPatchResult(RequestId Request, GraphPatch Patch, LossReport Loss) : AlgebraResult(Request);
public sealed record DeltaResult(RequestId Request, DeltaConversion Conversion) : AlgebraResult(Request);
public sealed record AlgebraRejected(RequestId Request, ImmutableArray<Diagnostic> Errors) : AlgebraResult(Request);
public interface IGraphChangeAlgebra
{
    IAsyncEnumerable<AlgebraResult> EvaluateAsync(IAsyncEnumerable<AlgebraRequest> requests,
        CancellationToken cancellationToken = default);
}

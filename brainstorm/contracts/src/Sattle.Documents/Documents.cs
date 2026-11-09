using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Sattle.Model;
using Sattle.Projections;
using Sattle.Runtime;
namespace Sattle.Documents;

// Stable block IDs = projection rule + source identity/group key, never row index.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Heading), "heading")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(DocumentTable), "table")]
[JsonDerivedType(typeof(BulletList), "list")]
[JsonDerivedType(typeof(CodeBlock), "code")]
public abstract record DocumentBlock(BlockId Id);
public sealed record Heading(BlockId Id, int Level, string Text) : DocumentBlock(Id);
public sealed record Paragraph(BlockId Id, string Text) : DocumentBlock(Id);
public sealed record TableRow(string Key, ImmutableArray<string> Cells);
public sealed record DocumentTable(BlockId Id, ImmutableArray<string> Columns,
    ImmutableArray<TableRow> Rows) : DocumentBlock(Id);
public sealed record BulletList(BlockId Id, ImmutableArray<string> Items) : DocumentBlock(Id);
public sealed record CodeBlock(BlockId Id, string Language, string Text) : DocumentBlock(Id);
public sealed record DocumentVersion(TenantId Tenant, DocumentId Document, Revision Revision);
public enum DocumentCompleteness { Complete, ExplicitlyTruncated }
public sealed record DocumentSnapshot(string SchemaVersion, DocumentVersion Version,
    string ProjectionVersion, ImmutableArray<GraphVersion> Sources,
    ImmutableArray<DocumentBlock> Blocks, DocumentCompleteness Completeness);
public sealed record BlockEdit(BlockId Id, DocumentBlock? Before, DocumentBlock? After);
// Order is explicit and independent of block identity. Both orders enable exact reversal.
public sealed record DocumentPatch(string SchemaVersion, DocumentVersion From, DocumentVersion To,
    ImmutableArray<GraphVersion> BeforeSources, ImmutableArray<GraphVersion> AfterSources,
    string BeforeProjectionVersion, string AfterProjectionVersion,
    ImmutableArray<BlockEdit> Edits, ImmutableArray<BlockId> BeforeOrder,
    ImmutableArray<BlockId> AfterOrder, DocumentCompleteness BeforeCompleteness,
    DocumentCompleteness AfterCompleteness);
public enum DocumentFormat { Markdown, Html, PlainText }
public enum MissingValue { Empty, LiteralUnknown, Reject }
public enum SortDirection { Ascending, Descending }
public sealed record SortKey(string Field, SortDirection Direction);
public enum BlockTemplateKind { Heading, Paragraph, Table, List, Code }
public sealed record DocumentTemplate(string RuleId, BlockTemplateKind Kind,
    OperatorId Rows, ImmutableArray<string> BlockIdentityFields, ImmutableArray<string> RowIdentityFields, ImmutableArray<SortKey> OrderBy,
    ImmutableArray<string> Fields, MissingValue Missing, int? HeadingLevel);
public enum OverflowPolicy { Reject, ExplicitTruncation }
public sealed record DocumentProjectionDefinition(string SchemaVersion, string Id, string Version,
    OntologyRef Ontology, ImmutableArray<InputBinding> Inputs, ImmutableArray<Operator> Operators,
    ImmutableArray<Wire> Wires, ImmutableArray<DocumentTemplate> Templates,
    long MaximumBlocks, OverflowPolicy Overflow);
public sealed record ProjectDocument(RequestId Request, TenantId Tenant, DocumentId Document, DocumentProjectionDefinition Definition,
    ImmutableArray<GraphVersion> Sources, WorkBudget Budget);
public sealed record DocumentResult(RequestId Request, DocumentSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Errors);
public interface IDocumentProjection
{
    IAsyncEnumerable<DocumentResult> ProjectAsync(IAsyncEnumerable<ProjectDocument> requests,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<DocumentChange> ObserveAsync(IAsyncEnumerable<FollowDocument> subscriptions,
        CancellationToken cancellationToken = default);
}
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DocumentStarted), "snapshot")]
[JsonDerivedType(typeof(DocumentChanged), "patch")]
[JsonDerivedType(typeof(DocumentUnavailable), "unavailable")]
public abstract record DocumentChange(RequestId Request);
public sealed record DocumentStarted(RequestId Request, DocumentSnapshot Snapshot) : DocumentChange(Request);
public sealed record DocumentChanged(RequestId Request, DocumentPatch Patch) : DocumentChange(Request);
public sealed record DocumentUnavailable(RequestId Request, ImmutableArray<Diagnostic> Errors) : DocumentChange(Request);
public sealed record RenderDocument(RequestId Request, DocumentSnapshot Snapshot,
    DocumentFormat Format, string RendererVersion);
// UTF-8 full replacement in v1. No claim that Markdown byte diffs obey graph diff algebra.
public sealed record RenderedDocument(RequestId Request, DocumentVersion Version,
    DocumentFormat Format, string MediaType, ImmutableArray<byte> Utf8, ContentHash Digest);
public interface IDocumentRenderer
{
    IAsyncEnumerable<RenderedDocument> RenderAsync(IAsyncEnumerable<RenderDocument> requests,
        CancellationToken cancellationToken = default);
}

public sealed record FollowGraph(string Port, GraphId Graph, GraphScope Scope);
public sealed record FollowDocument(RequestId Request, TenantId Tenant, DocumentId Document,
    DocumentProjectionDefinition Definition, ImmutableArray<FollowGraph> Sources,
    WorkBudget Budget, string? ResumeToken);

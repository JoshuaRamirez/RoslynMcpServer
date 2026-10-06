namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Maximum number of outline nodes to return (DFS pre-order budget). Omit for no cap (return full tree).
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Maximum outline tree depth to return (1 = root entries only). Entries deeper than this are dropped
    /// before the <see cref="MaxResults"/> node budget is applied. Omit for no depth cap (return full tree).
    /// </summary>
    public int? MaxDepth { get; init; }

    /// <summary>
    /// Optional symbol kind to keep (same values and case-insensitive parsing as <c>search_symbols</c>
    /// <c>kindFilter</c>: Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event,
    /// Constant, Local, Parameter, Namespace; <c>Local</c> and <c>Parameter</c> are accepted but never match an
    /// outline entry, so they return an empty outline). Only a single exact kind name is accepted. When set, an outline entry is kept if its kind matches or any descendant
    /// matches (non-matching ancestors are kept only as containers); subtrees with no match are dropped.
    /// Constructors match <c>Method</c> and enum members match <c>Constant</c>. Applied before
    /// <see cref="MaxDepth"/> and <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c>
    /// reflect the kind-filtered tree. An unrecognized value (including numeric
    /// values such as <c>"6"</c> and comma-separated combinations) fails with <c>InvalidSymbolKind</c>.
    /// Omit, empty, or whitespace for no kind filter (return every declaration).
    /// </summary>
    public string? KindFilter { get; init; }
}

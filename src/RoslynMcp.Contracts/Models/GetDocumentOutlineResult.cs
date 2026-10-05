namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineResult
{
    /// <summary>
    /// File path that was analyzed.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// Top-level outline entries (may be truncated when maxDepth and/or maxResults is set;
    /// maxDepth drops entries deeper than N first, then maxResults applies its DFS pre-order node budget).
    /// </summary>
    public required IReadOnlyList<OutlineEntry> Entries { get; init; }

    /// <summary>
    /// Total count of symbols in the outline (may exceed the node count in Entries if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated due to maxDepth and/or maxResults
    /// (true when <see cref="TotalCount"/> exceeds the node count in <see cref="Entries"/>).
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// A single entry in the document outline.
/// </summary>
public sealed class OutlineEntry
{
    /// <summary>
    /// Symbol name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Symbol kind (Class, Method, Property, etc.).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Accessibility modifier (public, private, etc.).
    /// </summary>
    public string? Accessibility { get; init; }

    /// <summary>
    /// Return type or type annotation (for methods, properties, fields).
    /// </summary>
    public string? ReturnType { get; init; }

    /// <summary>
    /// Child entries (methods inside class, etc.).
    /// </summary>
    public IReadOnlyList<OutlineEntry>? Children { get; init; }
}

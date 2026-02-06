namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a symbol search query.
/// </summary>
public sealed class SearchSymbolsResult
{
    /// <summary>
    /// The query that was searched.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Matching symbols.
    /// </summary>
    public required IReadOnlyList<SymbolSearchEntry> Symbols { get; init; }

    /// <summary>
    /// Total matches found (may exceed list if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated.
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// A single entry in symbol search results.
/// </summary>
public sealed class SymbolSearchEntry
{
    /// <summary>
    /// Simple name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Symbol kind.
    /// </summary>
    public required Enums.SymbolKind Kind { get; init; }

    /// <summary>
    /// File containing the symbol.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Containing type or namespace for context.
    /// </summary>
    public string? ContainerName { get; init; }
}

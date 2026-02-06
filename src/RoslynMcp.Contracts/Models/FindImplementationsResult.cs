namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a find implementations query.
/// </summary>
public sealed class FindImplementationsResult
{
    /// <summary>
    /// Name of the symbol that was searched.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name of the symbol.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// All implementations found.
    /// </summary>
    public required IReadOnlyList<ImplementationInfo> Implementations { get; init; }

    /// <summary>
    /// Total count found (may exceed list if truncated).
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated.
    /// </summary>
    public bool Truncated { get; init; }
}

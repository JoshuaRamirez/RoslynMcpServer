namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a find_callers query.
/// </summary>
public sealed class FindCallersResult
{
    /// <summary>
    /// Name of the target symbol.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// Fully qualified name of the target symbol.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// List of callers found.
    /// </summary>
    public required IReadOnlyList<CallerInfo> Callers { get; init; }

    /// <summary>
    /// Total count of callers found.
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether the result was truncated.
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Information about a caller of a symbol.
/// </summary>
public sealed class CallerInfo
{
    /// <summary>
    /// Name of the calling symbol.
    /// </summary>
    public required string CallerName { get; init; }

    /// <summary>
    /// Fully qualified name of the calling symbol.
    /// </summary>
    public required string CallerFullyQualifiedName { get; init; }

    /// <summary>
    /// Source file path.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// 1-based line number of the call site.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column number of the call site.
    /// </summary>
    public required int Column { get; init; }

    /// <summary>
    /// Context snippet around the call.
    /// </summary>
    public string? Snippet { get; init; }
}

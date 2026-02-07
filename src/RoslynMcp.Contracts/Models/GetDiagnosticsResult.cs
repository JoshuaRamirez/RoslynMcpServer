namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsResult
{
    /// <summary>
    /// List of diagnostics found.
    /// </summary>
    public required IReadOnlyList<DiagnosticInfo> Diagnostics { get; init; }

    /// <summary>
    /// Total count of diagnostics.
    /// </summary>
    public required int TotalCount { get; init; }
}

/// <summary>
/// Information about a single diagnostic.
/// </summary>
public sealed class DiagnosticInfo
{
    /// <summary>
    /// Diagnostic ID (e.g., CS0168).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Diagnostic message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Severity level (Error, Warning, Info, Hidden).
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Source file path.
    /// </summary>
    public string? File { get; init; }

    /// <summary>
    /// 1-based line number.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Diagnostic category.
    /// </summary>
    public string? Category { get; init; }
}

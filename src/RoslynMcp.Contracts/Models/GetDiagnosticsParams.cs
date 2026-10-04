namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsParams
{
    /// <summary>
    /// Absolute path to a source file to restrict diagnostics to. If null, returns all solution diagnostics.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// Minimum severity to include. Default: Warning (includes Error and Warning).
    /// Valid values: Error, Warning, Info, Hidden, All.
    /// </summary>
    public string? SeverityFilter { get; init; }

    /// <summary>
    /// Maximum number of diagnostics to return. Omit for no cap (return all matching diagnostics).
    /// </summary>
    public int? MaxResults { get; init; }
}

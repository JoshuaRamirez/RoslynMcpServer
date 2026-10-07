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

    /// <summary>
    /// Optional diagnostic IDs to restrict results to (e.g. CS0168, CS8019), matched case-insensitively.
    /// Omit or pass an empty list for no ID filter. Applied before <see cref="MaxResults"/>.
    /// </summary>
    public IReadOnlyList<string>? DiagnosticIds { get; init; }

    /// <summary>
    /// Optional diagnostic IDs to drop from results (e.g. CS1591, CS8019), matched case-insensitively.
    /// Omit or pass an empty list for no exclusion. Applied together with <see cref="DiagnosticIds"/>
    /// (exclusion wins when an ID is in both) and before <see cref="MaxResults"/>.
    /// </summary>
    public IReadOnlyList<string>? ExcludeDiagnosticIds { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict diagnostics to.
    /// Only projects whose file path equals it are compiled and scanned (every target-framework
    /// variant of a multi-targeted project shares that path, so all are included). Omit for the
    /// whole solution. Applied together with <see cref="SourceFile"/> and the ID / severity filters,
    /// and before <see cref="MaxResults"/>.
    /// </summary>
    public string? ProjectPath { get; init; }
}

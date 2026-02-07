namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Filter for diagnostic severity levels.
/// </summary>
public enum DiagnosticSeverityFilter
{
    /// <summary>Show only errors.</summary>
    Error,

    /// <summary>Show errors and warnings.</summary>
    Warning,

    /// <summary>Show errors, warnings, and info.</summary>
    Info,

    /// <summary>Show all diagnostics including hidden.</summary>
    Hidden,

    /// <summary>Show all diagnostics (alias for Hidden).</summary>
    All
}

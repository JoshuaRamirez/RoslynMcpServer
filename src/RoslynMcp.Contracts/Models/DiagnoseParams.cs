namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the diagnose tool.
/// </summary>
public sealed class DiagnoseParams
{
    /// <summary>
    /// Optional: solution to test loading.
    /// </summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Include detailed diagnostic information. Default: false.
    /// </summary>
    public bool Verbose { get; init; }
}

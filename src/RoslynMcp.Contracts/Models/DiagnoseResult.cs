using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of the diagnose operation.
/// </summary>
public sealed class DiagnoseResult
{
    /// <summary>
    /// Whether all components are healthy.
    /// </summary>
    public required bool Healthy { get; init; }

    /// <summary>
    /// Component health status.
    /// </summary>
    public required ComponentStatus Components { get; init; }

    /// <summary>
    /// Current workspace status.
    /// </summary>
    public required WorkspaceStatus Workspace { get; init; }

    /// <summary>
    /// Available tool capabilities.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>
    /// Any errors encountered.
    /// </summary>
    public required IReadOnlyList<RefactoringError> Errors { get; init; }

    /// <summary>
    /// Any warnings.
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Status of system components.
/// </summary>
public sealed class ComponentStatus
{
    /// <summary>
    /// Whether Roslyn assemblies are available.
    /// </summary>
    public required bool RoslynAvailable { get; init; }

    /// <summary>
    /// Roslyn version if available.
    /// </summary>
    public string? RoslynVersion { get; init; }

    /// <summary>
    /// Whether MSBuild was found.
    /// </summary>
    public required bool MsBuildFound { get; init; }

    /// <summary>
    /// MSBuild version if found.
    /// </summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>
    /// Whether .NET SDK is available.
    /// </summary>
    public required bool DotnetSdkAvailable { get; init; }

    /// <summary>
    /// .NET SDK version if available.
    /// </summary>
    public string? DotnetSdkVersion { get; init; }
}

/// <summary>
/// Status of the current workspace.
/// </summary>
public sealed class WorkspaceStatus
{
    /// <summary>
    /// Current workspace state.
    /// </summary>
    public required WorkspaceState State { get; init; }

    /// <summary>
    /// Whether a solution is loaded.
    /// </summary>
    public required bool SolutionLoaded { get; init; }

    /// <summary>
    /// Path to loaded solution (if any).
    /// </summary>
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Number of projects in solution.
    /// </summary>
    public int ProjectCount { get; init; }

    /// <summary>
    /// Number of documents across all projects.
    /// </summary>
    public int DocumentCount { get; init; }
}

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Factory for creating scoped workspace contexts.
/// Ensures proper MSBuildWorkspace lifecycle management.
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>
    /// Creates a workspace context for the given project/solution.
    /// Caller must dispose to release resources.
    /// </summary>
    /// <param name="projectOrSolutionPath">Path to .sln or .csproj file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Scoped workspace context.</returns>
    Task<WorkspaceContext> CreateContextAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the environment is properly configured for workspace loading.
    /// </summary>
    /// <returns>Diagnostic information about the environment.</returns>
    EnvironmentDiagnostics CheckEnvironment();
}

/// <summary>
/// Diagnostic information about the runtime environment.
/// </summary>
public sealed class EnvironmentDiagnostics
{
    /// <summary>
    /// Whether MSBuild was found.
    /// </summary>
    public required bool MsBuildFound { get; init; }

    /// <summary>
    /// MSBuild version if found.
    /// </summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>
    /// MSBuild path if found.
    /// </summary>
    public string? MsBuildPath { get; init; }

    /// <summary>
    /// .NET SDK version if found.
    /// </summary>
    public string? DotnetSdkVersion { get; init; }

    /// <summary>
    /// Any error message if environment check failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Paths that were searched for MSBuild.
    /// </summary>
    public IReadOnlyList<string>? SearchPaths { get; init; }
}

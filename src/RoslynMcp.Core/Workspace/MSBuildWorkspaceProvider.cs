using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;

namespace RoslynMcp.Core.Workspace;

/// <summary>
/// Creates MSBuildWorkspace instances with proper configuration.
/// </summary>
public sealed class MSBuildWorkspaceProvider : IWorkspaceProvider
{
    private static bool _msBuildRegistered;
    private static readonly object _registrationLock = new();
    private static VisualStudioInstance? _registeredInstance;

    private readonly IFileWriter _fileWriter;

    /// <summary>
    /// Creates a new workspace provider.
    /// </summary>
    /// <param name="fileWriter">Optional file writer for atomic operations.</param>
    public MSBuildWorkspaceProvider(IFileWriter? fileWriter = null)
    {
        _fileWriter = fileWriter ?? new AtomicFileWriter();
    }

    /// <inheritdoc />
    public async Task<WorkspaceContext> CreateContextAsync(
        string projectOrSolutionPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.MissingRequiredParam,
                "Project or solution path is required.");
        }

        if (!PathResolver.IsValidSolutionOrProjectPath(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.InvalidSourcePath,
                "Path must be a .sln or .csproj file.");
        }

        if (!File.Exists(projectOrSolutionPath))
        {
            throw new RefactoringException(
                ErrorCodes.SourceFileNotFound,
                $"File not found: {projectOrSolutionPath}");
        }

        EnsureMsBuildRegistered();

        var properties = new Dictionary<string, string>
        {
            ["CheckForSystemRuntimeDependency"] = "true",
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true"
        };

        var workspace = MSBuildWorkspace.Create(properties);

        // Collect workspace diagnostics but don't fail on warnings
        var diagnostics = new List<WorkspaceDiagnostic>();
        workspace.WorkspaceFailed += (_, args) =>
        {
            diagnostics.Add(args.Diagnostic);
        };

        Solution solution;
        var normalizedPath = PathResolver.NormalizePath(projectOrSolutionPath);

        if (normalizedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            solution = await workspace.OpenSolutionAsync(normalizedPath, cancellationToken: cancellationToken);
        }
        else
        {
            var project = await workspace.OpenProjectAsync(normalizedPath, cancellationToken: cancellationToken);
            solution = project.Solution;
        }

        // Check for critical errors
        var errors = diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).ToList();
        if (errors.Count > 0)
        {
            workspace.Dispose();
            throw new RefactoringException(
                ErrorCodes.SolutionLoadFailed,
                $"Failed to load solution: {string.Join("; ", errors.Select(e => e.Message))}");
        }

        return new WorkspaceContext(workspace, solution, normalizedPath, _fileWriter);
    }

    /// <inheritdoc />
    public EnvironmentDiagnostics CheckEnvironment()
    {
        try
        {
            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

            if (instances.Length == 0)
            {
                return new EnvironmentDiagnostics
                {
                    MsBuildFound = false,
                    ErrorMessage = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK."
                };
            }

            var preferred = SelectPreferredInstance(instances);

            return new EnvironmentDiagnostics
            {
                MsBuildFound = true,
                MsBuildVersion = preferred.Version.ToString(),
                MsBuildPath = preferred.MSBuildPath,
                DotnetSdkVersion = Environment.Version.ToString(),
                SearchPaths = instances.Select(i => i.MSBuildPath).ToList()
            };
        }
        catch (Exception ex)
        {
            return new EnvironmentDiagnostics
            {
                MsBuildFound = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (_msBuildRegistered || MSBuildLocator.IsRegistered)
        {
            _msBuildRegistered = true;
            return;
        }

        lock (_registrationLock)
        {
            if (_msBuildRegistered || MSBuildLocator.IsRegistered)
            {
                _msBuildRegistered = true;
                return;
            }

            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();

            if (instances.Length == 0)
            {
                // Try to find .NET SDK manually
                var sdkPath = FindDotNetSdk();
                if (sdkPath != null)
                {
                    MSBuildLocator.RegisterMSBuildPath(sdkPath);
                    _msBuildRegistered = true;
                    return;
                }

                throw new RefactoringException(
                    ErrorCodes.MsBuildNotFound,
                    "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.");
            }

            _registeredInstance = SelectPreferredInstance(instances);
            MSBuildLocator.RegisterInstance(_registeredInstance);
            _msBuildRegistered = true;
        }
    }

    private static string? FindDotNetSdk()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sdkBase = Path.Combine(programFiles, "dotnet", "sdk");

        if (!Directory.Exists(sdkBase))
        {
            return null;
        }

        // Find the latest SDK version
        var sdkVersions = Directory.GetDirectories(sdkBase)
            .Select(Path.GetFileName)
            .Where(d => d != null && char.IsDigit(d[0]))
            .OrderByDescending(v => v)
            .ToList();

        if (sdkVersions.Count == 0)
        {
            return null;
        }

        var latestSdk = Path.Combine(sdkBase, sdkVersions[0]!);
        return Directory.Exists(latestSdk) ? latestSdk : null;
    }

    private static VisualStudioInstance SelectPreferredInstance(VisualStudioInstance[] instances)
    {
        // Prefer .NET SDK over Visual Studio installations (more predictable)
        return instances
            .OrderByDescending(i => i.DiscoveryType == DiscoveryType.DotNetSdk)
            .ThenByDescending(i => i.Version)
            .First();
    }
}

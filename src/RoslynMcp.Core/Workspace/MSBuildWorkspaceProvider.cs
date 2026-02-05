using System.Collections.Concurrent;
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

    /// <summary>
    /// Optional logging callback for diagnostics.
    /// Set this to capture MSBuild registration and workspace loading events.
    /// </summary>
    public static Action<string>? LogCallback { get; set; }

    /// <summary>
    /// Optional error logging callback for diagnostics.
    /// Set this to capture errors and exceptions.
    /// </summary>
    public static Action<string, Exception?>? LogErrorCallback { get; set; }

    /// <summary>
    /// Default timeout for workspace loading operations (5 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultWorkspaceLoadTimeout = TimeSpan.FromMinutes(5);

    private readonly IFileWriter _fileWriter;
    private readonly TimeSpan _workspaceLoadTimeout;

    /// <summary>
    /// Creates a new workspace provider.
    /// </summary>
    /// <param name="fileWriter">Optional file writer for atomic operations.</param>
    /// <param name="workspaceLoadTimeout">
    /// Optional timeout for workspace loading operations.
    /// Defaults to <see cref="DefaultWorkspaceLoadTimeout"/> (5 minutes).
    /// </param>
    public MSBuildWorkspaceProvider(IFileWriter? fileWriter = null, TimeSpan? workspaceLoadTimeout = null)
    {
        _fileWriter = fileWriter ?? new AtomicFileWriter();
        _workspaceLoadTimeout = workspaceLoadTimeout ?? DefaultWorkspaceLoadTimeout;
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

        LogCallback?.Invoke($"Creating workspace for: {projectOrSolutionPath}");

        var properties = new Dictionary<string, string>
        {
            ["CheckForSystemRuntimeDependency"] = "true",
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true"
        };

        var workspace = MSBuildWorkspace.Create(properties);

        // Collect workspace diagnostics but don't fail on warnings
        // Using ConcurrentBag for thread-safe collection as events may fire from multiple threads
        var diagnostics = new ConcurrentBag<WorkspaceDiagnostic>();
        workspace.WorkspaceFailed += (_, args) =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                LogErrorCallback?.Invoke($"Workspace failure: {args.Diagnostic.Message}", null);
            }
            else
            {
                LogCallback?.Invoke($"Workspace warning: {args.Diagnostic.Message}");
            }
            diagnostics.Add(args.Diagnostic);
        };

        Solution solution;
        var normalizedPath = PathResolver.NormalizePath(projectOrSolutionPath);

        // Create a linked cancellation token that includes both the caller's token and a timeout
        using var timeoutCts = new CancellationTokenSource(_workspaceLoadTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            if (normalizedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                LogCallback?.Invoke($"Opening solution: {normalizedPath}");
                solution = await workspace.OpenSolutionAsync(normalizedPath, cancellationToken: linkedCts.Token);
                LogCallback?.Invoke($"Solution opened with {solution.ProjectIds.Count} project(s).");
            }
            else
            {
                LogCallback?.Invoke($"Opening project: {normalizedPath}");
                var project = await workspace.OpenProjectAsync(normalizedPath, cancellationToken: linkedCts.Token);
                solution = project.Solution;
                LogCallback?.Invoke($"Project opened: {project.Name}");
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var errorMsg = $"Workspace loading timed out after {_workspaceLoadTimeout.TotalMinutes:F0} minutes. " +
                "The solution may be too large or MSBuild may be stuck. " +
                "Consider loading a specific project instead of the entire solution.";
            LogErrorCallback?.Invoke(errorMsg, null);
            workspace.Dispose();
            throw new RefactoringException(
                ErrorCodes.SolutionLoadFailed,
                errorMsg);
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
            LogCallback?.Invoke("MSBuild already registered, skipping registration.");
            _msBuildRegistered = true;
            return;
        }

        lock (_registrationLock)
        {
            if (_msBuildRegistered || MSBuildLocator.IsRegistered)
            {
                LogCallback?.Invoke("MSBuild already registered (checked inside lock).");
                _msBuildRegistered = true;
                return;
            }

            LogCallback?.Invoke("Querying Visual Studio instances for MSBuild...");
            var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            LogCallback?.Invoke($"Found {instances.Length} MSBuild instance(s).");

            if (instances.Length == 0)
            {
                // Try to find .NET SDK manually
                LogCallback?.Invoke("No instances found, searching for .NET SDK manually...");
                var sdkPath = FindDotNetSdk();
                if (sdkPath != null)
                {
                    LogCallback?.Invoke($"Found .NET SDK at: {sdkPath}");
                    MSBuildLocator.RegisterMSBuildPath(sdkPath);
                    LogCallback?.Invoke("MSBuild registered via .NET SDK path.");
                    _msBuildRegistered = true;
                    return;
                }

                var errorMsg = "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK.";
                LogErrorCallback?.Invoke(errorMsg, null);
                throw new RefactoringException(
                    ErrorCodes.MsBuildNotFound,
                    errorMsg);
            }

            _registeredInstance = SelectPreferredInstance(instances);
            LogCallback?.Invoke($"Selected MSBuild instance: {_registeredInstance.Name} v{_registeredInstance.Version} at {_registeredInstance.MSBuildPath}");
            MSBuildLocator.RegisterInstance(_registeredInstance);
            LogCallback?.Invoke("MSBuild registered successfully.");
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

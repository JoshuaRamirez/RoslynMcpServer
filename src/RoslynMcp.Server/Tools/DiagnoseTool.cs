using System.Text.Json;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for environment diagnostics.
/// </summary>
public sealed class DiagnoseTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new diagnose tool.
    /// </summary>
    public DiagnoseTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "diagnose";

    /// <inheritdoc />
    public string Description => "Check the health of the Roslyn MCP server environment and workspace status.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Optional: solution to test loading"
            },
            verbose = new
            {
                type = "boolean",
                description = "Include detailed diagnostic information",
                @default = false
            }
        },
        additionalProperties = false
    };

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(JsonElement? arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var args = arguments.HasValue
                ? JsonSerializer.Deserialize<DiagnoseArgs>(arguments.Value.GetRawText(), _jsonOptions)
                : new DiagnoseArgs();

            args ??= new DiagnoseArgs();

            // Check environment
            var envDiag = _workspaceProvider.CheckEnvironment();

            var errors = new List<Contracts.Errors.RefactoringError>();
            var warnings = new List<string>();

            if (!envDiag.MsBuildFound)
            {
                errors.Add(Contracts.Errors.RefactoringError.Create(
                    Contracts.Errors.ErrorCodes.MsBuildNotFound,
                    envDiag.ErrorMessage ?? "MSBuild not found"));
            }

            // Check Roslyn
            var roslynVersion = typeof(Microsoft.CodeAnalysis.Compilation).Assembly.GetName().Version?.ToString();

            // Try to load solution if provided
            WorkspaceStatus workspaceStatus;
            if (!string.IsNullOrEmpty(args.SolutionPath))
            {
                try
                {
                    using var context = await _workspaceProvider.CreateContextAsync(
                        args.SolutionPath,
                        cancellationToken);

                    workspaceStatus = new WorkspaceStatus
                    {
                        State = WorkspaceState.Ready,
                        SolutionLoaded = true,
                        SolutionPath = context.LoadedPath,
                        ProjectCount = context.Solution.Projects.Count(),
                        DocumentCount = context.Solution.Projects.Sum(p => p.Documents.Count())
                    };
                }
                catch (Exception ex)
                {
                    workspaceStatus = new WorkspaceStatus
                    {
                        State = WorkspaceState.Error,
                        SolutionLoaded = false
                    };
                    errors.Add(Contracts.Errors.RefactoringError.Create(
                        Contracts.Errors.ErrorCodes.SolutionLoadFailed,
                        ex.Message));
                }
            }
            else
            {
                workspaceStatus = new WorkspaceStatus
                {
                    State = WorkspaceState.Unloaded,
                    SolutionLoaded = false
                };
            }

            var capabilities = envDiag.MsBuildFound
                ? new[] { "move_type_to_file", "move_type_to_namespace", "diagnose" }
                : new[] { "diagnose" };

            var result = new DiagnoseResult
            {
                Healthy = envDiag.MsBuildFound && errors.Count == 0,
                Components = new ComponentStatus
                {
                    RoslynAvailable = true,
                    RoslynVersion = roslynVersion,
                    MsBuildFound = envDiag.MsBuildFound,
                    MsBuildVersion = envDiag.MsBuildVersion,
                    DotnetSdkAvailable = !string.IsNullOrEmpty(envDiag.DotnetSdkVersion),
                    DotnetSdkVersion = envDiag.DotnetSdkVersion
                },
                Workspace = workspaceStatus,
                Capabilities = capabilities,
                Errors = errors,
                Warnings = warnings
            };

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return ToolResult.Success(json);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                healthy = false,
                error = new { code = "INTERNAL_ERROR", message = ex.Message }
            }, _jsonOptions);
            return ToolResult.Error(json);
        }
    }

    private sealed class DiagnoseArgs
    {
        public string? SolutionPath { get; init; }
        public bool? Verbose { get; init; }
    }
}

using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for rename_namespace operation.
/// </summary>
public sealed class RenameNamespaceTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new rename-namespace tool.
    /// </summary>
    public RenameNamespaceTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "rename_namespace";

    /// <inheritdoc />
    public string Description =>
        "Rename a C# namespace across the solution, updating declarations, using directives, and qualified name references. Does not move folders unless updateFolders is true (not implemented in this version).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "namespaceName", "newName" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the .sln or .csproj file"
            },
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to a source file that declares the namespace"
            },
            namespaceName = new
            {
                type = "string",
                description = "Current namespace name (simple or fully qualified)"
            },
            newName = new
            {
                type = "string",
                description = "New namespace name (simple or fully qualified)"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number used to select a namespace declaration when the file has more than one",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number used with line when multiple declarations share a line",
                minimum = 1
            },
            updateFolders = new
            {
                type = "boolean",
                description = "Also move folders to match the new namespace. Default false. True is rejected because folder moves are not implemented.",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying",
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
            if (arguments == null)
            {
                return ToolResult.Error("Arguments required");
            }

            var args = JsonSerializer.Deserialize<RenameNamespaceArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new RenameNamespaceOperation(context);
            var @params = new RenameNamespaceParams
            {
                SourceFile = args.SourceFile,
                NamespaceName = args.NamespaceName,
                NewName = args.NewName,
                Line = args.Line,
                Column = args.Column,
                UpdateFolders = args.UpdateFolders ?? false,
                Preview = args.Preview ?? false
            };

            var result = await operation.ExecuteAsync(@params, cancellationToken);

            var json = JsonSerializer.Serialize(result, _jsonOptions);
            return result.Success ? ToolResult.Success(json) : ToolResult.Error(json);
        }
        catch (RefactoringException ex)
        {
            var error = ex.ToError();
            var json = JsonSerializer.Serialize(new { success = false, error }, _jsonOptions);
            return ToolResult.Error(json);
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code = "INTERNAL_ERROR", message = ex.Message }
            }, _jsonOptions);
            return ToolResult.Error(json);
        }
    }

    private sealed class RenameNamespaceArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string NamespaceName { get; init; } = "";
        public string NewName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? UpdateFolders { get; init; }
        public bool? Preview { get; init; }
    }
}

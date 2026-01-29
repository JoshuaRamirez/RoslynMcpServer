using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for move_type_to_namespace operation.
/// </summary>
public sealed class MoveTypeToNamespaceTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new move type to namespace tool.
    /// </summary>
    public MoveTypeToNamespaceTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "move_type_to_namespace";

    /// <inheritdoc />
    public string Description => "Change the namespace of a C# type. Updates all using directives and qualified references.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "symbolName", "targetNamespace" },
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
                description = "Absolute path to the source file containing the type"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the type to move (simple or fully qualified)"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation if multiple types match",
                minimum = 1
            },
            targetNamespace = new
            {
                type = "string",
                description = "Target namespace (e.g., MyApp.Services)"
            },
            updateFileLocation = new
            {
                type = "boolean",
                description = "Also move file to match namespace folder structure",
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

            var args = JsonSerializer.Deserialize<MoveTypeToNamespaceArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            // Create workspace context
            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            // Execute operation
            var operation = new MoveTypeToNamespaceOperation(context);
            var @params = new MoveTypeToNamespaceParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                TargetNamespace = args.TargetNamespace,
                UpdateFileLocation = args.UpdateFileLocation ?? false,
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

    private sealed class MoveTypeToNamespaceArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string SymbolName { get; init; } = "";
        public int? Line { get; init; }
        public string TargetNamespace { get; init; } = "";
        public bool? UpdateFileLocation { get; init; }
        public bool? Preview { get; init; }
    }
}

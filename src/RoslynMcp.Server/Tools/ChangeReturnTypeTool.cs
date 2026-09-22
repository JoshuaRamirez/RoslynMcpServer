using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for change_return_type operation.
/// </summary>
public sealed class ChangeReturnTypeTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new change return type tool.
    /// </summary>
    public ChangeReturnTypeTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "change_return_type";

    /// <inheritdoc />
    public string Description => "Change a method's return type and update return statements, overrides, and interface implementations. column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick. allFiles: true walks every C# file and changes the return type of every eligible method whose current return type can safely become newReturnType (sourceFile optional when true; cannot be combined with methodName, line, or column; newReturnType remains required).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "newReturnType" },
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
                description = "Absolute path to the source file containing the method. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "When true, walk every C# document (or the optional single sourceFile) and change the return type of every eligible method. Cannot be combined with methodName, line, or column. Default false.",
                @default = false
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to modify. Single-site only; cannot be combined with allFiles."
            },
            newReturnType = new
            {
                type = "string",
                description = "New return type (C# type syntax). Required for both single-site and allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based). Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the smallest method whose identifier or declaration span covers that column. Omitted keeps today's MethodName and/or Line start-line pick. Single-site only; cannot be combined with allFiles."
            },
            updateOverrides = new
            {
                type = "boolean",
                description = "Update the virtual/override chain together. Valid with allFiles.",
                @default = true
            },
            updateImplementations = new
            {
                type = "boolean",
                description = "Update interface declarations and implementations together. Valid with allFiles.",
                @default = true
            },
            convertReturnStatements = new
            {
                type = "boolean",
                description = "Attempt to convert return statements to the new type. Valid with allFiles.",
                @default = true
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying. Valid with allFiles.",
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

            var args = JsonSerializer.Deserialize<ChangeReturnTypeArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ChangeReturnTypeOperation(context);
            var @params = new ChangeReturnTypeParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                MethodName = args.MethodName,
                NewReturnType = args.NewReturnType,
                Line = args.Line,
                Column = args.Column,
                UpdateOverrides = args.UpdateOverrides ?? true,
                UpdateImplementations = args.UpdateImplementations ?? true,
                ConvertReturnStatements = args.ConvertReturnStatements ?? true,
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

    private sealed class ChangeReturnTypeArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? MethodName { get; init; }
        public string NewReturnType { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? UpdateOverrides { get; init; }
        public bool? UpdateImplementations { get; init; }
        public bool? ConvertReturnStatements { get; init; }
        public bool? Preview { get; init; }
    }
}

using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for inline_variable operation.
/// </summary>
public sealed class InlineVariableTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new inline variable tool.
    /// </summary>
    public InlineVariableTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "inline_variable";

    /// <inheritdoc />
    public string Description =>
        "Inline a local variable by replacing all usages with its initializer value. column (optional) picks the declaration whose identifier or declaration span covers that column. Omitted keeps today's variableName + optional line start-line pick. allFiles: true walks every C# file and inlines every eligible local (sourceFile optional when true; cannot be combined with variableName, line, or column).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath" },
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
                description = "Absolute path to the source file. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with variableName, line, or column.",
                @default = false
            },
            variableName = new
            {
                type = "string",
                description = "Name of the variable to inline. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number where the variable is declared (1-based). Optional for disambiguation. Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the declaration whose identifier or declaration span covers that column. Omitted keeps today's variableName + optional line start-line pick. Single-site only; cannot be combined with allFiles."
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

            var args = JsonSerializer.Deserialize<InlineVariableArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new InlineVariableOperation(context);
            var @params = new InlineVariableParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                VariableName = args.VariableName,
                Line = args.Line,
                Column = args.Column,
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

    private sealed class InlineVariableArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? VariableName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}

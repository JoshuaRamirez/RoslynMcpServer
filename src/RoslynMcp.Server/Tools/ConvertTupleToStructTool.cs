using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_tuple_to_struct operation.
/// </summary>
public sealed class ConvertTupleToStructTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert-tuple-to-struct tool.
    /// </summary>
    public ConvertTupleToStructTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "convert_tuple_to_struct";

    /// <inheritdoc />
    public string Description =>
        "Convert a tuple ((int X, int Y) / (1, 2) / ValueTuple) to a named struct and replace same-shape tuple creations in the solution. column (optional) picks the tuple creation whose span covers that column when set with line (exclusive-end; unique covering match, else CannotConvert / SymbolAmbiguous); omitted keeps today's line pick.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "line", "newTypeName" },
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
                description = "Absolute path to the source file containing the tuple expression"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number of the tuple expression. When column is omitted, matching stays today's line pick (single covering candidate returns; several on the line stay SymbolAmbiguous)."
            },
            newTypeName = new
            {
                type = "string",
                description = "Name of the struct to create"
            },
            column = new
            {
                type = "integer",
                description = "1-based column on the tuple expression. When set with line, selects the creation whose span covers that column (exclusive-end; today's unique covering match, else CannotConvert / SymbolAmbiguous). Omitted keeps today's line pick."
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

            var args = JsonSerializer.Deserialize<ConvertTupleToStructArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertTupleToStructOperation(context);
            var @params = new ConvertTupleToStructParams
            {
                SourceFile = args.SourceFile,
                Line = args.Line ?? 0,
                NewTypeName = args.NewTypeName,
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

    private sealed class ConvertTupleToStructArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int? Line { get; init; }
        public string NewTypeName { get; init; } = "";
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}

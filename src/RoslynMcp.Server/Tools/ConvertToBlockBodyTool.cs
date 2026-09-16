using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_to_block_body operation.
/// </summary>
public sealed class ConvertToBlockBodyTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert-to-block-body tool.
    /// </summary>
    public ConvertToBlockBodyTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "convert_to_block_body";

    /// <inheritdoc />
    public string Description =>
        "Convert a selected expression-bodied C# member (=> expr) to a block body. Methods become { return expr; } or { expr; } as appropriate; properties and accessors that are expression-bodied are converted too. column (optional) picks the member whose identifier or declaration span covers that column on the given line. Omitted keeps today's memberName and/or line pick (smallest containing node). Preview describes the rewrite and writes nothing. allFiles: true walks every C# file and converts every eligible expression-bodied member (sourceFile optional when true; cannot be combined with memberName, line, or column).";

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
                description = "Absolute path to the source file. Required when allFiles is false. Optional when allFiles is true to limit the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with memberName, line, or column.",
                @default = false
            },
            memberName = new
            {
                type = "string",
                description = "Name of the member to convert. Single-member only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number of the member (1-based). Required when memberName is omitted. Single-member only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the member whose identifier or declaration span covers that column on the given line. Omitted keeps today's memberName and/or line pick. Single-member only; cannot be combined with allFiles."
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

            var args = JsonSerializer.Deserialize<ConvertToBlockBodyArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertToBlockBodyOperation(context);
            var @params = new ConvertToBlockBodyParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                MemberName = args.MemberName,
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

    private sealed class ConvertToBlockBodyArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? MemberName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}

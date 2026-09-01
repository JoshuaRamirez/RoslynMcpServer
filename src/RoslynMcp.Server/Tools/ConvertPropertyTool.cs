using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_property operation.
/// </summary>
public sealed class ConvertPropertyTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert property tool.
    /// </summary>
    public ConvertPropertyTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "convert_property";

    /// <inheritdoc />
    public string Description =>
        "Convert a C# property between auto-property and full property with backing field. column (optional) picks the smallest property whose identifier or declaration span covers that column on the given line. Omitted keeps today's propertyName and/or line start-line pick. Preview describes the rewrite and writes nothing. allFiles: true walks every C# file and converts every distinct eligible property (sourceFile optional when true; cannot be combined with propertyName, line, or column).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "direction" },
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
                description = "Absolute path to the source file. Required when allFiles is false."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with propertyName, line, or column.",
                @default = false
            },
            direction = new
            {
                type = "string",
                description = "Conversion direction: 'ToFullProperty' or 'ToAutoProperty'"
            },
            propertyName = new
            {
                type = "string",
                description = "Name of the property to convert. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple properties have the same name (1-based). Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the smallest property whose identifier or declaration span covers that column on the given line. Omitted keeps today's propertyName and/or line start-line pick. Single-site only; cannot be combined with allFiles.",
                minimum = 1
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

            var args = JsonSerializer.Deserialize<ConvertPropertyArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertPropertyOperation(context);
            var @params = new ConvertPropertyParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                Direction = args.Direction,
                PropertyName = args.PropertyName,
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

    private sealed class ConvertPropertyArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string Direction { get; init; } = "";
        public string? PropertyName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}

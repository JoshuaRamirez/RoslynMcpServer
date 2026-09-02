using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for inline_constant operation.
/// </summary>
public sealed class InlineConstantTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new inline constant tool.
    /// </summary>
    public InlineConstantTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "inline_constant";

    /// <inheritdoc />
    public string Description =>
        "Inline a const field by replacing references with its literal value. Optionally remove the constant. line (optional) picks the matching const field whose identifier or declaration span covers that line when several constants share the name; omitted keeps today's constantName + optional typeName path including SymbolAmbiguous. column (optional) picks the matching const field whose identifier or declaration span covers that 1-based column when set with line (identifier preferred, then smallest covering declarator/field); omitted keeps today's constantName + optional typeName + optional line pick; column without line keeps today's omitted-line path after the name/typeName filter. allFiles: true walks every C# file and inlines every eligible const field (sourceFile optional when true; cannot be combined with constantName, typeName, line, or column).";

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
                description = "Absolute path to the source file containing the constant. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with constantName, typeName, line, or column.",
                @default = false
            },
            constantName = new
            {
                type = "string",
                description = "Name of the constant field to inline. Single-site only; cannot be combined with allFiles."
            },
            typeName = new
            {
                type = "string",
                description = "Containing type name for disambiguation when multiple constants share a name. Additive filter when supplied; line/column do not replace it. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for disambiguation when several constants share the name. When set, selects the const field whose identifier or declaration span covers that line (identifier preferred, then smallest covering declarator/field). Omitted keeps today's constantName + optional typeName path including SymbolAmbiguous. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set with line, selects the const field whose identifier or declaration span covers that column (identifier preferred, then smallest covering declarator/field). Omitted keeps today's constantName + optional typeName + optional line pick. Column without line keeps today's omitted-line path after the name/typeName filter. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            removeConstant = new
            {
                type = "boolean",
                description = "Remove the constant declaration after inlining when no remaining references exist",
                @default = true
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

            var args = JsonSerializer.Deserialize<InlineConstantArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new InlineConstantOperation(context);
            var @params = new InlineConstantParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                ConstantName = args.ConstantName,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
                RemoveConstant = args.RemoveConstant ?? true,
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

    private sealed class InlineConstantArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? ConstantName { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? RemoveConstant { get; init; }
        public bool? Preview { get; init; }
    }
}

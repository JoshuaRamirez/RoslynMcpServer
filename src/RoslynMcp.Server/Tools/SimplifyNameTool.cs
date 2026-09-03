using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for simplify_name operation.
/// </summary>
public sealed class SimplifyNameTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new simplify-name tool.
    /// </summary>
    public SimplifyNameTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "simplify_name";

    /// <inheritdoc />
    public string Description =>
        "Remove redundant namespace qualifications from type (and similar) references when a using directive or the current namespace already makes the short name bind to the same symbol. Process a single file or all files in the solution. scope is file (default; every eligible qualified name in the file) or location (the qualified name at line / optional column; single-file only). column (optional) picks the name whose span covers that column when set with line (exclusive-end; FirstOrDefault among covering names); omitted keeps today's first/leftmost-name-on-line-by-SpanStart pick; column without line keeps today's required-line validation. Names that would become ambiguous or bind differently are skipped and reported. Preview describes the simplifications and writes nothing.";

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
                description = "Absolute path to the source file. Required when allFiles is false."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with scope=location.",
                @default = false
            },
            line = new
            {
                type = "integer",
                description = "1-based line of the qualified name (required when scope is location). When column is omitted, matching stays today's first/leftmost name on the line by SpanStart.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column on the qualified name. When set with line, selects the name whose span covers that column (exclusive-end; today's FirstOrDefault among covering names). Omitted keeps today's first/leftmost-name-on-line-by-SpanStart pick. Column without line keeps today's required-line validation.",
                minimum = 1
            },
            scope = new
            {
                type = "string",
                description = "Scope of the operation: file (default) or location",
                @enum = new[] { "file", "location" },
                @default = "file"
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

            var args = JsonSerializer.Deserialize<SimplifyNameArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new SimplifyNameOperation(context);
            var @params = new SimplifyNameParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                Line = args.Line,
                Column = args.Column,
                Scope = args.Scope ?? "file",
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

    private sealed class SimplifyNameArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? Scope { get; init; }
        public bool? Preview { get; init; }
    }
}

using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for add_braces operation.
/// </summary>
public sealed class AddBracesTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new add-braces tool.
    /// </summary>
    public AddBracesTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "add_braces";

    /// <inheritdoc />
    public string Description =>
        "Add braces to control statements (if, else, for, foreach, while, using) that have a single-statement body, preserving semantics. Process a single file or all files in the solution. scope is statement (default; wrap the body at line/column; single-file only), file (every braceless control body in the file), or type (bodies inside typeName; single-file only). column (optional) picks the control statement whose keyword span covers that column when set with line (exclusive-end; shortest keyword / First among covering); omitted keeps today's first/leftmost-keyword-on-line-by-SpanStart pick; column without line keeps today's required-line validation. allFiles: true walks every C# file at file scope (omitted scope uses file) and cannot be combined with scope=statement or scope=type.";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with scope=statement or scope=type.",
                @default = false
            },
            line = new
            {
                type = "integer",
                description = "1-based line of the control-statement keyword (required when scope is statement). When column is omitted, matching stays today's first/leftmost keyword on the line by SpanStart.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column on the control-statement keyword. When set with line, selects the statement whose keyword span covers that column (exclusive-end; today's shortest keyword / First among covering). Omitted keeps today's first/leftmost-keyword-on-line-by-SpanStart pick. Column without line keeps today's required-line validation.",
                minimum = 1
            },
            scope = new
            {
                type = "string",
                description = "Scope of the operation: statement (default; single-file only), file, or type (single-file only)",
                @enum = new[] { "statement", "file", "type" },
                @default = "statement"
            },
            typeName = new
            {
                type = "string",
                description = "Type name when scope is type"
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

            var args = JsonSerializer.Deserialize<AddBracesArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new AddBracesOperation(context);
            var @params = new AddBracesParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                Line = args.Line,
                Column = args.Column,
                Scope = args.Scope,
                TypeName = args.TypeName,
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

    private sealed class AddBracesArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? Scope { get; init; }
        public string? TypeName { get; init; }
        public bool? Preview { get; init; }
    }
}

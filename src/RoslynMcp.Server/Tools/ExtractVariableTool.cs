using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for extract_variable operation.
/// </summary>
public sealed class ExtractVariableTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new extract variable tool.
    /// </summary>
    public ExtractVariableTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "extract_variable";

    /// <inheritdoc />
    public string Description =>
        "Extract an expression to a local variable. sourceFile, startLine, startColumn, endLine, endColumn, and variableName are required when allFiles is omitted or false. allFiles: true walks every C# file and extracts every eligible outermost non-trivial expression (variable named from expression text; sourceFile optional when true; cannot be combined with startLine, startColumn, endLine, endColumn, or variableName). useVar / replaceAll / preview remain valid with allFiles where they apply (sites that cannot honor them are skipped).";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with startLine, startColumn, endLine, endColumn, or variableName.",
                @default = false
            },
            startLine = new
            {
                type = "integer",
                description = "Start line of the expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            startColumn = new
            {
                type = "integer",
                description = "Start column of the expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endLine = new
            {
                type = "integer",
                description = "End line of the expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endColumn = new
            {
                type = "integer",
                description = "End column of the expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            variableName = new
            {
                type = "string",
                description = "Name for the new variable. Required when allFiles is false. Single-site only; cannot be combined with allFiles. When allFiles is true, each variable is named from its expression text."
            },
            useVar = new
            {
                type = "boolean",
                description = "Use var instead of explicit type. Valid with allFiles.",
                @default = true
            },
            replaceAll = new
            {
                type = "boolean",
                description = "Replace all equivalent occurrences in the same containing method or block. Valid with allFiles.",
                @default = false
            },
            preview = new
            {
                type = "boolean",
                description = "Return computed changes without applying. Valid with allFiles.",
                @default = false
            }
        },
        oneOf = new object[]
        {
            new
            {
                properties = new
                {
                    allFiles = new
                    {
                        @enum = new[] { false }
                    }
                },
                required = new[] { "solutionPath", "sourceFile", "startLine", "startColumn", "endLine", "endColumn", "variableName" }
            },
            new
            {
                properties = new
                {
                    allFiles = new
                    {
                        @const = true
                    }
                },
                required = new[] { "solutionPath", "allFiles" },
                not = new
                {
                    anyOf = new object[]
                    {
                        new { required = new[] { "startLine" } },
                        new { required = new[] { "startColumn" } },
                        new { required = new[] { "endLine" } },
                        new { required = new[] { "endColumn" } },
                        new { required = new[] { "variableName" } }
                    }
                }
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

            var args = JsonSerializer.Deserialize<ExtractVariableArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ExtractVariableOperation(context);
            var @params = new ExtractVariableParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                StartLine = args.StartLine,
                StartColumn = args.StartColumn,
                EndLine = args.EndLine,
                EndColumn = args.EndColumn,
                VariableName = args.VariableName,
                UseVar = args.UseVar ?? true,
                ReplaceAll = args.ReplaceAll ?? false,
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

    private sealed class ExtractVariableArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? StartLine { get; init; }
        public int? StartColumn { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
        public string? VariableName { get; init; }
        public bool? UseVar { get; init; }
        public bool? ReplaceAll { get; init; }
        public bool? Preview { get; init; }
    }
}

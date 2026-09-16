using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for inline_method operation.
/// </summary>
public sealed class InlineMethodTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new inline method tool.
    /// </summary>
    public InlineMethodTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "inline_method";

    /// <inheritdoc />
    public string Description =>
        "Inline a method by replacing call sites with the method body. Optionally remove the method. column (optional) picks the smallest method whose identifier or declaration span covers that column. Omitted keeps today's MethodName and/or Line identifier start-line pick. allFiles: true walks every C# file and inlines every eligible method (sourceFile optional when true; cannot be combined with methodName, line, column, or callSiteLocation).";

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
                description = "Absolute path to the source file containing the method. Required when allFiles is false. When allFiles is true, optional and limits the walk to that one file."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with methodName, line, column, or callSiteLocation.",
                @default = false
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to inline. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number of the method declaration (1-based). Optional for disambiguation. Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the smallest method whose identifier or declaration span covers that column. Omitted keeps today's MethodName and/or Line identifier start-line pick. Single-site only; cannot be combined with allFiles."
            },
            callSiteLocation = new
            {
                type = "object",
                description = "When set, inline only this call site and leave the method in place. Single-site only; cannot be combined with allFiles.",
                properties = new
                {
                    file = new
                    {
                        type = "string",
                        description = "Absolute path to the file containing the call site"
                    },
                    line = new
                    {
                        type = "integer",
                        description = "1-based line of the call site"
                    },
                    column = new
                    {
                        type = "integer",
                        description = "1-based column of the call site"
                    }
                },
                required = new[] { "file", "line", "column" }
            },
            removeMethod = new
            {
                type = "boolean",
                description = "Remove the method after inlining all call sites. Valid with allFiles.",
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

            var args = JsonSerializer.Deserialize<InlineMethodArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new InlineMethodOperation(context);
            var @params = new InlineMethodParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                MethodName = args.MethodName,
                Line = args.Line,
                Column = args.Column,
                CallSiteLocation = args.CallSiteLocation,
                RemoveMethod = args.RemoveMethod ?? true,
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

    private sealed class InlineMethodArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? MethodName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public CallSiteLocation? CallSiteLocation { get; init; }
        public bool? RemoveMethod { get; init; }
        public bool? Preview { get; init; }
    }
}

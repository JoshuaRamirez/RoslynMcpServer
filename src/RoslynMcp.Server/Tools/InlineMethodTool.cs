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
    public string Description => "Inline a method by replacing call sites with the method body. Optionally remove the method.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "methodName" },
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
                description = "Absolute path to the source file containing the method"
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to inline"
            },
            line = new
            {
                type = "integer",
                description = "Line number of the method declaration (1-based). Optional for disambiguation."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the smallest method whose identifier or declaration span covers that column. Omitted keeps today's MethodName and/or Line start-line pick."
            },
            callSiteLocation = new
            {
                type = "object",
                description = "When set, inline only this call site and leave the method in place.",
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
                description = "Remove the method after inlining all call sites",
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
        public string SourceFile { get; init; } = "";
        public string MethodName { get; init; } = "";
        public int? Line { get; init; }
        public int? Column { get; init; }
        public CallSiteLocation? CallSiteLocation { get; init; }
        public bool? RemoveMethod { get; init; }
        public bool? Preview { get; init; }
    }
}

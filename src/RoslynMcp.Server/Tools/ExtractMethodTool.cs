using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for extract_method operation.
/// </summary>
public sealed class ExtractMethodTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new extract method tool.
    /// </summary>
    public ExtractMethodTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "extract_method";

    /// <inheritdoc />
    public string Description => "Extract selected code into a new method. Automatically detects parameters and return values.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "startLine", "startColumn", "endLine", "endColumn", "methodName" },
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
                description = "Absolute path to the source file"
            },
            startLine = new
            {
                type = "integer",
                description = "1-based start line of selection",
                minimum = 1
            },
            startColumn = new
            {
                type = "integer",
                description = "1-based start column of selection",
                minimum = 1
            },
            endLine = new
            {
                type = "integer",
                description = "1-based end line of selection",
                minimum = 1
            },
            endColumn = new
            {
                type = "integer",
                description = "1-based end column of selection",
                minimum = 1
            },
            methodName = new
            {
                type = "string",
                description = "Name for the new method"
            },
            visibility = new
            {
                type = "string",
                description = "Visibility for the new method",
                @enum = new[] { "private", "internal", "protected", "public" },
                @default = "private"
            },
            makeStatic = new
            {
                type = "boolean",
                description = "Force the method to be static (otherwise auto-detected)"
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

            var args = JsonSerializer.Deserialize<ExtractMethodArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            // Create workspace context
            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            // Execute operation
            var operation = new ExtractMethodOperation(context);
            var @params = new ExtractMethodParams
            {
                SourceFile = args.SourceFile,
                StartLine = args.StartLine,
                StartColumn = args.StartColumn,
                EndLine = args.EndLine,
                EndColumn = args.EndColumn,
                MethodName = args.MethodName,
                Visibility = args.Visibility ?? "private",
                MakeStatic = args.MakeStatic,
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

    private sealed class ExtractMethodArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int StartLine { get; init; }
        public int StartColumn { get; init; }
        public int EndLine { get; init; }
        public int EndColumn { get; init; }
        public string MethodName { get; init; } = "";
        public string? Visibility { get; init; }
        public bool? MakeStatic { get; init; }
        public bool? Preview { get; init; }
    }
}

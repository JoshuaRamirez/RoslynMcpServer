using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for extract_constant operation.
/// </summary>
public sealed class ExtractConstantTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new extract constant tool.
    /// </summary>
    public ExtractConstantTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "extract_constant";

    /// <inheritdoc />
    public string Description => "Extract a literal value to a named constant.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "startLine", "startColumn", "endLine", "endColumn", "constantName" },
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
                description = "Start line of the literal (1-based)"
            },
            startColumn = new
            {
                type = "integer",
                description = "Start column of the literal (1-based)"
            },
            endLine = new
            {
                type = "integer",
                description = "End line of the literal (1-based)"
            },
            endColumn = new
            {
                type = "integer",
                description = "End column of the literal (1-based)"
            },
            constantName = new
            {
                type = "string",
                description = "Name for the new constant"
            },
            visibility = new
            {
                type = "string",
                @enum = new[] { "private", "protected", "internal", "public" },
                description = "Visibility of the constant",
                @default = "private"
            },
            replaceAll = new
            {
                type = "boolean",
                description = "Replace all occurrences of the same literal in the class",
                @default = false
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

            var args = JsonSerializer.Deserialize<ExtractConstantArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ExtractConstantOperation(context);
            var @params = new ExtractConstantParams
            {
                SourceFile = args.SourceFile,
                StartLine = args.StartLine,
                StartColumn = args.StartColumn,
                EndLine = args.EndLine,
                EndColumn = args.EndColumn,
                ConstantName = args.ConstantName,
                Visibility = args.Visibility ?? "private",
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

    private sealed class ExtractConstantArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int StartLine { get; init; }
        public int StartColumn { get; init; }
        public int EndLine { get; init; }
        public int EndColumn { get; init; }
        public string ConstantName { get; init; } = "";
        public string? Visibility { get; init; }
        public bool? ReplaceAll { get; init; }
        public bool? Preview { get; init; }
    }
}

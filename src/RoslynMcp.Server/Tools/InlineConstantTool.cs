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
    public string Description => "Inline a const field by replacing references with its literal value. Optionally remove the constant.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "constantName" },
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
                description = "Absolute path to the source file containing the constant"
            },
            constantName = new
            {
                type = "string",
                description = "Name of the constant field to inline"
            },
            typeName = new
            {
                type = "string",
                description = "Containing type name for disambiguation when multiple constants share a name"
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
                ConstantName = args.ConstantName,
                TypeName = args.TypeName,
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
        public string SourceFile { get; init; } = "";
        public string ConstantName { get; init; } = "";
        public string? TypeName { get; init; }
        public bool? RemoveConstant { get; init; }
        public bool? Preview { get; init; }
    }
}

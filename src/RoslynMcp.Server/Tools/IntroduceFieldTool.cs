using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for introduce_field operation.
/// </summary>
public sealed class IntroduceFieldTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new introduce field tool.
    /// </summary>
    public IntroduceFieldTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "introduce_field";

    /// <inheritdoc />
    public string Description => "Turn a selected local variable or expression into a class field, optionally initializing it in a constructor.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile", "startLine", "startColumn", "endLine", "endColumn", "fieldName" },
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
                description = "Start line of the local variable or expression (1-based)"
            },
            startColumn = new
            {
                type = "integer",
                description = "Start column of the local variable or expression (1-based)"
            },
            endLine = new
            {
                type = "integer",
                description = "End line of the local variable or expression (1-based)"
            },
            endColumn = new
            {
                type = "integer",
                description = "End column of the local variable or expression (1-based)"
            },
            fieldName = new
            {
                type = "string",
                description = "Name for the new field"
            },
            isReadonly = new
            {
                type = "boolean",
                description = "Create as a readonly field",
                @default = false
            },
            isStatic = new
            {
                type = "boolean",
                description = "Create as a static field",
                @default = false
            },
            initializeInConstructor = new
            {
                type = "boolean",
                description = "Initialize the field in a constructor instead of inline",
                @default = false
            },
            replaceAll = new
            {
                type = "boolean",
                description = "Replace all identical expressions in the containing type",
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

            var args = JsonSerializer.Deserialize<IntroduceFieldArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new IntroduceFieldOperation(context);
            var @params = new IntroduceFieldParams
            {
                SourceFile = args.SourceFile,
                StartLine = args.StartLine,
                StartColumn = args.StartColumn,
                EndLine = args.EndLine,
                EndColumn = args.EndColumn,
                FieldName = args.FieldName,
                IsReadonly = args.IsReadonly ?? false,
                IsStatic = args.IsStatic ?? false,
                InitializeInConstructor = args.InitializeInConstructor ?? false,
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

    private sealed class IntroduceFieldArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int StartLine { get; init; }
        public int StartColumn { get; init; }
        public int EndLine { get; init; }
        public int EndColumn { get; init; }
        public string FieldName { get; init; } = "";
        public bool? IsReadonly { get; init; }
        public bool? IsStatic { get; init; }
        public bool? InitializeInConstructor { get; init; }
        public bool? ReplaceAll { get; init; }
        public bool? Preview { get; init; }
    }
}

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
    public string Description =>
        "Turn a selected local variable or expression into a class field, optionally initializing it in a constructor. allFiles: true walks every C# file and promotes every eligible local (field named from that local; sourceFile optional when true; cannot be combined with startLine, startColumn, endLine, endColumn, or fieldName). isReadonly / isStatic / initializeInConstructor / replaceAll / preview remain valid with allFiles where they apply (sites that cannot honor them are skipped).";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with startLine, startColumn, endLine, endColumn, or fieldName.",
                @default = false
            },
            startLine = new
            {
                type = "integer",
                description = "Start line of the local variable or expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            startColumn = new
            {
                type = "integer",
                description = "Start column of the local variable or expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endLine = new
            {
                type = "integer",
                description = "End line of the local variable or expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endColumn = new
            {
                type = "integer",
                description = "End column of the local variable or expression (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            fieldName = new
            {
                type = "string",
                description = "Name for the new field. Required when allFiles is false. Single-site only; cannot be combined with allFiles. When allFiles is true, each field is named from its local."
            },
            isReadonly = new
            {
                type = "boolean",
                description = "Create as a readonly field. Valid with allFiles.",
                @default = false
            },
            isStatic = new
            {
                type = "boolean",
                description = "Create as a static field. Valid with allFiles.",
                @default = false
            },
            initializeInConstructor = new
            {
                type = "boolean",
                description = "Initialize the field in a constructor instead of inline. Valid with allFiles.",
                @default = false
            },
            replaceAll = new
            {
                type = "boolean",
                description = "Replace all identical expressions in the containing type (single-site expression path). Valid with allFiles but ignored for local promote.",
                @default = false
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
                AllFiles = args.AllFiles ?? false,
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
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? StartLine { get; init; }
        public int? StartColumn { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
        public string? FieldName { get; init; }
        public bool? IsReadonly { get; init; }
        public bool? IsStatic { get; init; }
        public bool? InitializeInConstructor { get; init; }
        public bool? ReplaceAll { get; init; }
        public bool? Preview { get; init; }
    }
}

using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for safe_delete operation.
/// </summary>
public sealed class SafeDeleteTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new safe-delete tool.
    /// </summary>
    public SafeDeleteTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "safe_delete";

    /// <inheritdoc />
    public string Description =>
        "Delete a selected symbol only when it has no remaining references. If usages exist, reject with their locations. sourceFile, startLine, startColumn, endLine, and endColumn are required when allFiles is omitted or false. allFiles: true walks every C# file and deletes every eligible unused private (or local) declaration under today's single-site rules (public / protected / internal skipped in bulk) (sourceFile optional when true; cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName). preview remains valid with allFiles.";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Deletes every eligible unused private (or local) declaration. Cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName.",
                @default = false
            },
            startLine = new
            {
                type = "integer",
                description = "Start line of the selected symbol (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            startColumn = new
            {
                type = "integer",
                description = "Start column of the selected symbol (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endLine = new
            {
                type = "integer",
                description = "End line of the selected symbol (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            endColumn = new
            {
                type = "integer",
                description = "End column of the selected symbol (1-based). Required when allFiles is false. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            symbolName = new
            {
                type = "string",
                description = "Optional symbol name used to confirm the selection. Single-site only; cannot be combined with allFiles."
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
                required = new[] { "solutionPath", "sourceFile", "startLine", "startColumn", "endLine", "endColumn" }
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
                        new { required = new[] { "symbolName" } }
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

            var args = JsonSerializer.Deserialize<SafeDeleteArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new SafeDeleteOperation(context);
            var @params = new SafeDeleteParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                StartLine = args.StartLine,
                StartColumn = args.StartColumn,
                EndLine = args.EndLine,
                EndColumn = args.EndColumn,
                SymbolName = args.SymbolName,
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

    private sealed class SafeDeleteArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public int? StartLine { get; init; }
        public int? StartColumn { get; init; }
        public int? EndLine { get; init; }
        public int? EndColumn { get; init; }
        public string? SymbolName { get; init; }
        public bool? Preview { get; init; }
    }
}

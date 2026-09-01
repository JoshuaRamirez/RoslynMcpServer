using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for convert_to_async operation.
/// </summary>
public sealed class ConvertToAsyncTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new convert to async tool.
    /// </summary>
    public ConvertToAsyncTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "convert_to_async";

    /// <inheritdoc />
    public string Description =>
        "Convert a synchronous method to async/await pattern. column (optional) picks the method whose identifier or declaration span covers that column. Omitted keeps today's MethodName + Line pick. renameToAsync (default true) rewrites call-site identifiers to the Async name. updateCallers (default false) wraps already-async callers in await and skips synchronous callers that cannot legally await. Preview describes caller updates and writes nothing. allFiles: true walks every C# file and converts every distinct eligible sync method (sourceFile optional when true; cannot be combined with methodName, line, or column).";

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
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with methodName, line, or column.",
                @default = false
            },
            methodName = new
            {
                type = "string",
                description = "Name of the method to convert. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "Line number for disambiguation if multiple methods have the same name (1-based). Single-site only; cannot be combined with allFiles."
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set, selects the method whose identifier or declaration span covers that column. Omitted keeps today's MethodName + Line pick. Single-site only; cannot be combined with allFiles."
            },
            renameToAsync = new
            {
                type = "boolean",
                description = "Rename method by adding Async suffix",
                @default = true
            },
            updateCallers = new
            {
                type = "boolean",
                description = "Update callers to await the converted method. Default false leaves callers unaugmented (identifier rename still applies when renameToAsync is true). True wraps already-async callers in await and skips synchronous callers that cannot legally await.",
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

            var args = JsonSerializer.Deserialize<ConvertToAsyncArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new ConvertToAsyncOperation(context);
            var @params = new ConvertToAsyncParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                MethodName = args.MethodName,
                Line = args.Line,
                Column = args.Column,
                RenameToAsync = args.RenameToAsync ?? true,
                UpdateCallers = args.UpdateCallers ?? false,
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

    private sealed class ConvertToAsyncArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? MethodName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? RenameToAsync { get; init; }
        public bool? UpdateCallers { get; init; }
        public bool? Preview { get; init; }
    }
}

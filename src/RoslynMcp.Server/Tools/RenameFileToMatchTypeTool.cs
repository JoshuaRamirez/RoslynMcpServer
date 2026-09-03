using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for rename_file_to_match_type operation.
/// </summary>
public sealed class RenameFileToMatchTypeTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new rename-file-to-match-type tool.
    /// </summary>
    public RenameFileToMatchTypeTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "rename_file_to_match_type";

    /// <inheritdoc />
    public string Description =>
        "Rename a source file so its name matches the primary type declared in it, without renaming the type or its references. Process a single file or all files in the solution. allFiles: true walks every C# file and renames each unambiguous mismatched single-type file (sourceFile optional when true; cannot be combined with typeName, line, or column). column (optional) picks the smallest type whose identifier or declaration span covers that column when set with line (identifier preferred, then smallest covering declaration); omitted keeps today's typeName + optional line pick; column without line keeps today's omitted-line path.";

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
                description = "Absolute path to the source file to rename. Required when allFiles is false."
            },
            allFiles = new
            {
                type = "boolean",
                description = "Process all C# files in the solution. When true, sourceFile is optional. Cannot be combined with typeName, line, or column.",
                @default = false
            },
            typeName = new
            {
                type = "string",
                description = "Type name used to disambiguate when the file declares more than one type. Single-site only; cannot be combined with allFiles."
            },
            line = new
            {
                type = "integer",
                description = "1-based line number used to select a type when the file declares more than one. When column is omitted, matching stays today's covering-span line pick. Single-site only; cannot be combined with allFiles.",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column for disambiguation. When set with line, selects the smallest type whose identifier or declaration span covers that column (identifier preferred, then smallest covering declaration). Omitted keeps today's typeName + optional line pick. Column without line keeps today's omitted-line path. Single-site only; cannot be combined with allFiles.",
                minimum = 1
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

            var args = JsonSerializer.Deserialize<RenameFileToMatchTypeArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
            {
                return ToolResult.Error("Failed to parse arguments");
            }

            using var context = await _workspaceProvider.CreateContextAsync(
                args.SolutionPath,
                cancellationToken);

            var operation = new RenameFileToMatchTypeOperation(context);
            var @params = new RenameFileToMatchTypeParams
            {
                SourceFile = args.SourceFile,
                AllFiles = args.AllFiles ?? false,
                TypeName = args.TypeName,
                Line = args.Line,
                Column = args.Column,
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

    private sealed class RenameFileToMatchTypeArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public bool? AllFiles { get; init; }
        public string? TypeName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public bool? Preview { get; init; }
    }
}

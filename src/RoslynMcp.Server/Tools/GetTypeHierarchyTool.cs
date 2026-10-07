using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_type_hierarchy query.
/// </summary>
public sealed class GetTypeHierarchyTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get type hierarchy tool.
    /// </summary>
    public GetTypeHierarchyTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "get_type_hierarchy";

    /// <inheritdoc />
    public string Description => "Get the type hierarchy for a C# type: base types (ancestors), derived types (descendants), and implemented interfaces. Optional maxResults caps the returned DerivedTypes list (omit = no cap). BaseTypes and Interfaces are not capped. sourceFile locates the target type; optional derivedFile restricts reported DerivedTypes to those declared in that file (applied before maxResults, so totalCount/truncated reflect the filtered set; BaseTypes and Interfaces are not filtered); optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported DerivedTypes to those declared in documents of that project, combined with derivedFile and applied before maxResults (omit = whole solution); optional transitive (default true) set to false returns only direct subclasses (types whose immediate base type is the target) instead of every descendant, with derivedFile/projectPath/maxResults applied to that direct set (BaseTypes and Interfaces unaffected).";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "sourceFile" },
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
                description = "Absolute path to the source file containing the type"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the type"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for position-based resolution",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number for position-based resolution",
                minimum = 1
            },
            direction = new
            {
                type = "string",
                description = "Hierarchy direction: Ancestors, Descendants, or Both (default: Both)"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of derived types to return (optional; omit = no cap). Does not cap BaseTypes or Interfaces.",
                minimum = 1
            },
            derivedFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict reported DerivedTypes to those declared in that file (optional; omit for the whole solution). Does not filter BaseTypes or Interfaces."
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict reported DerivedTypes to those declared in a document of that project (optional; omit for the whole solution). Every target framework of a multi-targeted project is included; combined with derivedFile and applied before maxResults so totalCount/truncated reflect the project-scoped set; does not filter BaseTypes or Interfaces; sourceFile may be in another project; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
            },
            transitive = new
            {
                type = "boolean",
                description = "Include every descendant in DerivedTypes (default true). false returns only direct subclasses (types whose immediate base type is the target type); derivedFile/projectPath filter and maxResults cap that direct set, so totalCount/truncated reflect it. Does not affect BaseTypes or Interfaces"
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
                return ToolResult.Error("Arguments required");

            var args = JsonSerializer.Deserialize<GetTypeHierarchyArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetTypeHierarchyOperation(context);
            var @params = new GetTypeHierarchyParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                Direction = args.Direction,
                MaxResults = args.MaxResults,
                DerivedFile = args.DerivedFile,
                ProjectPath = args.ProjectPath,
                Transitive = args.Transitive
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

    private sealed class GetTypeHierarchyArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public string? Direction { get; init; }
        public int? MaxResults { get; init; }
        public string? DerivedFile { get; init; }
        public string? ProjectPath { get; init; }
        public bool? Transitive { get; init; }
    }
}

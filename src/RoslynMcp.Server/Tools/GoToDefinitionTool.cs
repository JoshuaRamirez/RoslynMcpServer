using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for go_to_definition query.
/// </summary>
public sealed class GoToDefinitionTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new go to definition tool.
    /// </summary>
    public GoToDefinitionTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "go_to_definition";

    /// <inheritdoc />
    public string Description => "Navigate to a C# symbol's definition. Returns the file, line, and column where the symbol is declared, including support for partial classes with multiple locations. Optional maxResults caps the returned Definitions list (omit = no cap). sourceFile locates the symbol; optional definitionFile restricts reported Definitions to locations in that file (applied before maxResults, so totalCount/truncated reflect the filtered set; metadata definitions are excluded when set); optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported Definitions to locations in documents of that project, combined with definitionFile and applied before maxResults (omit = unchanged; metadata definitions are excluded when set).";

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
                description = "Absolute path to the source file containing the symbol reference"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the symbol to navigate to"
            },
            line = new
            {
                type = "integer",
                description = "1-based line number for position-based symbol resolution",
                minimum = 1
            },
            column = new
            {
                type = "integer",
                description = "1-based column number for position-based symbol resolution",
                minimum = 1
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of definition locations to return (optional; omit = no cap).",
                minimum = 1
            },
            definitionFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict reported Definitions to locations in that file (optional; omit to keep the existing unfiltered behavior). Metadata definitions are excluded when set."
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict reported Definitions to locations in a document of that project (optional; omit to keep the existing unfiltered behavior). Every target framework of a multi-targeted project is included; combined with definitionFile and applied before maxResults so totalCount/truncated reflect the project-scoped set; metadata definitions are excluded when set; sourceFile may be in another project; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
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

            var args = JsonSerializer.Deserialize<GoToDefinitionArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GoToDefinitionOperation(context);
            var @params = new GoToDefinitionParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                MaxResults = args.MaxResults,
                DefinitionFile = args.DefinitionFile,
                ProjectPath = args.ProjectPath
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

    private sealed class GoToDefinitionArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? MaxResults { get; init; }
        public string? DefinitionFile { get; init; }
        public string? ProjectPath { get; init; }
    }
}

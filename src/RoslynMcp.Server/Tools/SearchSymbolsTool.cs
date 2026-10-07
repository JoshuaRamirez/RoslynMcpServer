using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for search_symbols query.
/// </summary>
public sealed class SearchSymbolsTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new search symbols tool.
    /// </summary>
    public SearchSymbolsTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "search_symbols";

    /// <inheritdoc />
    public string Description => "Search for C# symbols by name pattern across the solution or a specific file. Supports substring matching and filtering by symbol kind (Class, Method, Property, etc.). Optional sourceFile restricts results to symbols declared in that file. Optional caseSensitive (default false) set to true matches query case-sensitively (ordinal) instead of case-insensitively. Optional exactMatch (default false) set to true returns only symbols whose whole name equals query instead of containing it. Optional namespaceFilter restricts results to symbols declared in that namespace or a namespace nested inside it. Optional projectPath (absolute .csproj path of a project in the loaded solution) searches only that project, so only symbols declared in its sources are returned.";

    /// <inheritdoc />
    public object InputSchema => new
    {
        type = "object",
        required = new[] { "solutionPath", "query" },
        properties = new
        {
            solutionPath = new
            {
                type = "string",
                description = "Absolute path to the .sln or .csproj file"
            },
            query = new
            {
                type = "string",
                description = "Name pattern to search for (substring matching)"
            },
            kindFilter = new
            {
                type = "string",
                description = "Filter by symbol kind: Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of results to return (default: 50)",
                minimum = 1
            },
            sourceFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict results to symbols declared in that file (optional)"
            },
            caseSensitive = new
            {
                type = "boolean",
                description = "Match query case-sensitively (ordinal) when true (default false = case-insensitive substring match); applied before maxResults so totalCount/truncated reflect the case-sensitive set"
            },
            exactMatch = new
            {
                type = "boolean",
                description = "Match query against the whole symbol name when true (default false = substring match); honors caseSensitive and is applied before maxResults so totalCount/truncated reflect the exact-match set"
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict results to (optional; omit for all namespaces). Keeps symbols whose containing namespace equals it or is nested inside it (e.g. App.Services matches App.Services and App.Services.Orders, not App.ServicesExtra); ordinal (case-sensitive) comparison, leading global:: accepted; global-namespace symbols never match a named filter, while a bare global:: is the root namespace and filters nothing; applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict results to symbols declared in that project (optional; omit for the whole solution). Only that project is compiled and searched (every target framework of a multi-targeted project); combined with the other filters and applied before maxResults so totalCount/truncated reflect the project-scoped set; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
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

            var args = JsonSerializer.Deserialize<SearchSymbolsArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new SearchSymbolsOperation(context);
            var @params = new SearchSymbolsParams
            {
                Query = args.Query,
                KindFilter = args.KindFilter,
                MaxResults = args.MaxResults,
                SourceFile = args.SourceFile,
                CaseSensitive = args.CaseSensitive,
                ExactMatch = args.ExactMatch,
                NamespaceFilter = args.NamespaceFilter,
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

    private sealed class SearchSymbolsArgs
    {
        public string SolutionPath { get; init; } = "";
        public string Query { get; init; } = "";
        public string? KindFilter { get; init; }
        public int? MaxResults { get; init; }
        public string? SourceFile { get; init; }
        public bool? CaseSensitive { get; init; }
        public bool? ExactMatch { get; init; }
        public string? NamespaceFilter { get; init; }
        public string? ProjectPath { get; init; }
    }
}

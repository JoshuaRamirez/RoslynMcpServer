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
    public string Description => "Search for C# symbols by name pattern across the solution or a specific file. Supports substring matching and filtering by symbol kind (Class, Method, Property, etc.). Optional sourceFile restricts results to symbols declared in that file.";

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
                SourceFile = args.SourceFile
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
    }
}

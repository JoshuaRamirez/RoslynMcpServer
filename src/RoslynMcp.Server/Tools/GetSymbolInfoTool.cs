using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_symbol_info query.
/// </summary>
public sealed class GetSymbolInfoTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get symbol info tool.
    /// </summary>
    public GetSymbolInfoTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "get_symbol_info";

    /// <inheritdoc />
    public string Description => "Get detailed information about a C# symbol including its type hierarchy, members, modifiers, parameters, return type, and XML documentation. Optional kindFilter (same values as search_symbols / get_document_outline kindFilter, case-insensitive) keeps only Members of that kind on a type, applied before maxResults so totalCount/truncated reflect the filtered set; Local/Parameter/Namespace never match a type member. Optional maxResults caps the returned Members list of a type (omit = no cap); result includes TotalCount and Truncated. Interfaces, Parameters and other fields are not filtered or capped, and kindFilter/maxResults have no effect on non-type symbols.";

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
                description = "Absolute path to the source file containing the symbol"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the symbol to examine"
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
                description = "Maximum number of members to return for a type symbol (optional; omit = no cap). Applied after kindFilter so totalCount/truncated reflect the kind-filtered set. Does not cap Interfaces or Parameters; no effect on non-type symbols.",
                minimum = 1
            },
            kindFilter = new
            {
                type = "string",
                description = "Keep only Members of this kind on a type symbol (optional; case-insensitive): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; numeric or comma-combined values are rejected with InvalidSymbolKind). Same values and parsing as search_symbols / get_document_outline kindFilter. Applied before maxResults so totalCount/truncated reflect the filtered set; Local/Parameter/Namespace never match a type member (empty Members); omit, empty, or whitespace = all members; no effect on non-type symbols or on Interfaces/Parameters"
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

            var args = JsonSerializer.Deserialize<GetSymbolInfoArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetSymbolInfoOperation(context);
            var @params = new GetSymbolInfoParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                MaxResults = args.MaxResults,
                KindFilter = args.KindFilter
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

    private sealed class GetSymbolInfoArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? MaxResults { get; init; }
        public string? KindFilter { get; init; }
    }
}

using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for find_callers query.
/// </summary>
public sealed class FindCallersTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new find callers tool.
    /// </summary>
    public FindCallersTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "find_callers";

    /// <inheritdoc />
    public string Description => "Find all callers of a C# method or symbol across the solution. Returns the calling symbol name, file location, and a code snippet for each call site. sourceFile locates the target symbol; optional callerFile restricts reported call sites to callers located in that file (applied before maxResults, so totalCount/truncated reflect the filtered set). Optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported call sites to documents in that project, applied before maxResults. Optional namespaceFilter restricts reported call sites to those whose containing namespace (the code enclosing each call site, not the target symbol) equals it or is nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as search_symbols / find_implementations / find_references namespaceFilter), combined with the other filters and applied before maxResults. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy accessibilityFilter) keeps only call sites whose calling symbol's DeclaredAccessibility equals it exactly (implicit defaults count: a class member with no modifier is private), combined with the other filters and applied before maxResults. Optional nameFilter (ordinal, case-insensitive substring; surrounding whitespace trimmed; same matching as get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_references nameFilter) keeps only call sites whose calling symbol (the same CallingSymbol accessibilityFilter judges) has a simple name containing it, combined with the other filters and applied before maxResults.";

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
                description = "Name of the symbol to find callers for"
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
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of callers to return",
                minimum = 1
            },
            callerFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict reported call sites to callers located in that file (optional; omit for the whole solution)"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict reported call sites to those whose document belongs to that project (optional; omit for the whole solution). Every target framework of a multi-targeted project is included; combined with callerFile and applied before maxResults so totalCount/truncated reflect the project-scoped set; sourceFile may be in another project; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict reported call sites to (optional; omit for all namespaces). Keeps call sites whose call site (the member, type, or namespace declaration enclosing the location; not the target symbol's namespace) lives in a namespace that equals it or is nested inside it, e.g. App.Services matches App.Services.Orders but not App.ServicesExtra; ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace sites never match a named filter. Same matching as search_symbols / find_implementations / find_references namespaceFilter; combined with callerFile/projectPath/accessibilityFilter/nameFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Declared accessibility to restrict reported call sites to (optional; omit for all accessibilities): public, internal, protected, private, protected internal, or private protected (case-insensitive; surrounding whitespace trimmed and inner whitespace runs collapsed; internal protected / protected private also accepted). Keeps call sites whose calling symbol's DeclaredAccessibility equals it exactly (implicit defaults count: a class member with no modifier is private; protected internal is not matched by protected or internal). Same values and InvalidVisibility error as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy accessibilityFilter; combined with callerFile/projectPath/namespaceFilter/nameFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            nameFilter = new
            {
                type = "string",
                description = "Name substring to restrict reported call sites to (optional; omit, empty, or whitespace for all names). Keeps call sites whose calling symbol (the same CallingSymbol accessibilityFilter judges) has a simple name containing it (ordinal, case-insensitive; surrounding whitespace trimmed; inner whitespace not collapsed), e.g. Handle for callers named HandleOrder or OrderHandler. The matched name is exactly the reported callerName (both use the calling symbol's ISymbol.Name); a call inside a property accessor body reports the property itself, so it matches as the property name (e.g. Total, not get_Total). Same matching as get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_references nameFilter; combined with callerFile/projectPath/namespaceFilter/accessibilityFilter and applied before maxResults so totalCount/truncated reflect the filtered set; no match returns an empty success"
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

            var args = JsonSerializer.Deserialize<FindCallersArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new FindCallersOperation(context);
            var @params = new FindCallersParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                MaxResults = args.MaxResults,
                CallerFile = args.CallerFile,
                ProjectPath = args.ProjectPath,
                NamespaceFilter = args.NamespaceFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                NameFilter = args.NameFilter
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

    private sealed class FindCallersArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? MaxResults { get; init; }
        public string? CallerFile { get; init; }
        public string? ProjectPath { get; init; }
        public string? NamespaceFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? NameFilter { get; init; }
    }
}

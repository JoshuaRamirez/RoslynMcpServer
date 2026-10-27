using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_document_outline query.
/// </summary>
public sealed class GetDocumentOutlineTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get document outline tool.
    /// </summary>
    public GetDocumentOutlineTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "get_document_outline";

    /// <inheritdoc />
    public string Description => "Get a structured outline of all declarations in a C# file: namespaces, types, methods, properties, fields, and events with their line numbers and accessibility. Optional maxDepth caps the returned outline tree depth (1 = top-level entries only; omit = no depth cap). Optional maxResults caps the returned outline tree via DFS pre-order (omit = no cap), applied after maxDepth. Optional kindFilter (same values as search_symbols kindFilter, case-insensitive) keeps only entries of that kind plus their containing types/namespaces, applied before maxDepth and maxResults. Optional nameFilter keeps only entries whose name contains the value (case-insensitive substring) plus their containing types/namespaces; combined with kindFilter an entry must satisfy both; applied before maxDepth and maxResults. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive) keeps only entries with exactly that declared accessibility, counting implicit defaults (a top-level type with no modifier is internal, a class member with no modifier is private), plus their containing types/namespaces; combined with kindFilter and nameFilter an entry must satisfy all; applied before maxDepth and maxResults. Optional namespaceFilter keeps only entries declared in that namespace or one nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as search_symbols / get_type_hierarchy namespaceFilter; namespace entries match on the namespace itself) plus their containing types/namespaces; combined with kindFilter, nameFilter, and accessibilityFilter an entry must satisfy all; applied before maxDepth and maxResults. Optional staticFilter (boolean) keeps only entries whose declared symbol is static when true (ISymbol.IsStatic: static methods, properties, fields, events, static constructors, static classes; const fields and enum members count as static) or only non-static entries when false (instance members and constructors, finalizers, non-static types, delegates), plus their containing types/namespaces; namespaces never match either value; combined with kindFilter, nameFilter, accessibilityFilter, and namespaceFilter an entry must satisfy all; applied before maxDepth and maxResults; omit = all declarations. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR.";

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
                description = "Absolute path to the source file"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of outline nodes to return via DFS pre-order (optional; omit = no cap)",
                minimum = 1
            },
            maxDepth = new
            {
                type = "integer",
                description = "Maximum outline tree depth to return; 1 = top-level entries only (optional; omit = no depth cap). Applied before maxResults.",
                minimum = 1
            },
            kindFilter = new
            {
                type = "string",
                description = "Keep only outline entries of this kind plus their containing types/namespaces (optional; case-insensitive): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; numeric or comma-combined values are rejected with InvalidSymbolKind). Constructors match Method and enum members match Constant; Local and Parameter are accepted but never match an outline entry (empty outline). Applied before maxDepth and maxResults; omit = all declarations."
            },
            nameFilter = new
            {
                type = "string",
                description = "Keep only outline entries whose name contains this value (optional; ordinal, case-insensitive substring) plus their containing types/namespaces; subtrees with no match are dropped. Combined with kindFilter, an entry must satisfy both. Applied before maxDepth and maxResults; omit, empty, or whitespace = all declarations."
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Keep only outline entries with exactly this declared accessibility plus their containing types/namespaces (optional; omit, empty, or whitespace = any accessibility): public, internal, protected, private, protected internal, or private protected (case-insensitive, surrounding whitespace trimmed; internal protected / protected private also accepted). Same values and semantics as search_symbols accessibilityFilter: implicit defaults count (a top-level type with no modifier is internal, a class member with no modifier is private, an interface or enum member is public) and protected internal is not matched by protected or internal; namespaces never match. Combined with kindFilter and nameFilter (an entry must satisfy all); applied before maxDepth and maxResults; any other value returns an invalid-visibility error."
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Keep only outline entries declared in this namespace or one nested inside it plus their containing types/namespaces (optional; omit for all namespaces). Type and member entries match when their containing namespace equals it or is nested inside it; namespace entries match when the namespace itself does (e.g. App.Services matches the App.Services and App.Services.Orders entries and their types/members, not App.ServicesExtra); ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace declarations never match a named filter. Same matching as search_symbols namespaceFilter; combined with kindFilter, nameFilter, and accessibilityFilter (an entry must satisfy all); applied before maxDepth and maxResults so totalCount/truncated reflect the filtered tree"
            },
            staticFilter = new
            {
                type = "boolean",
                description = "Keep only outline entries whose declared symbol is static when true (ISymbol.IsStatic; const fields and enum members count as static) or only non-static entries when false, plus their containing types/namespaces (optional; omit = all declarations). Namespaces never match either value. Combined with kindFilter, nameFilter, accessibilityFilter, and namespaceFilter (an entry must satisfy all); applied before maxDepth and maxResults so totalCount/truncated reflect the filtered tree; no match returns an empty outline. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR."
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

            var args = JsonSerializer.Deserialize<GetDocumentOutlineArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetDocumentOutlineOperation(context);
            var @params = new GetDocumentOutlineParams
            {
                SourceFile = args.SourceFile,
                MaxResults = args.MaxResults,
                MaxDepth = args.MaxDepth,
                KindFilter = args.KindFilter,
                NameFilter = args.NameFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                NamespaceFilter = args.NamespaceFilter,
                StaticFilter = args.StaticFilter
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

    private sealed class GetDocumentOutlineArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public int? MaxResults { get; init; }
        public int? MaxDepth { get; init; }
        public string? KindFilter { get; init; }
        public string? NameFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? NamespaceFilter { get; init; }
        public bool? StaticFilter { get; init; }
    }
}

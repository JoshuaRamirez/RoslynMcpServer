using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for get_diagnostics query.
/// </summary>
public sealed class GetDiagnosticsTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new get diagnostics tool.
    /// </summary>
    public GetDiagnosticsTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "get_diagnostics";

    /// <inheritdoc />
    public string Description => "Get compiler diagnostics (errors, warnings) for the solution or a specific file. Useful for checking compilation status before or after refactoring. Optional diagnosticIds restricts results to the given IDs (e.g. CS0168; case-insensitive; omit = all IDs). Optional excludeDiagnosticIds drops the given IDs (e.g. CS1591; case-insensitive; wins over diagnosticIds on overlap; omit = exclude none). Optional projectPath restricts results to one project of the solution (absolute .csproj path; omit = whole solution). Optional namespaceFilter restricts results to diagnostics whose diagnostic site — the member / type / namespace declaration enclosing the diagnostic location — lives in a namespace that equals it or is nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as find_references / find_callers namespaceFilter; global-namespace sites and diagnostics with no source location never match), combined with the other filters and applied before maxResults. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / find_references accessibilityFilter) keeps only diagnostics whose diagnostic site's DeclaredAccessibility equals it exactly (implicit defaults count: a class member with no modifier is private; declaration-attached diagnostics such as CS0169 resolve to the declared member; lambdas / local functions looked through to declaring member; namespace-level sites and diagnostics with no source location never match), combined with the other filters and applied before maxResults. Optional messageFilter keeps only diagnostics whose message contains it (ordinal, case-insensitive substring; surrounding whitespace trimmed; same matching as get_document_outline / find_references nameFilter; diagnostics with no source location are included when their message matches; omit, empty, or whitespace = no message filter), combined with the other filters and applied before maxResults. Optional kindFilter (a single SymbolKind name such as Method, Field, Property, Class; case-insensitive; same values and parsing as search_symbols / find_references / find_callers kindFilter) keeps only diagnostics whose diagnostic site — the same site accessibilityFilter / namespaceFilter resolve (declaration-attached members such as CS0169 on a field; otherwise the enclosing member / type, with lambdas / local functions looked through to the declaring member; methods / constructors / accessors → Method; fields → Field or Constant; namespace-level sites and diagnostics with no source location never match) — has that kind via SymbolKindMapper, combined with the other filters and applied before maxResults (invalid → InvalidSymbolKind; omit / blank = no kind filter; no match → empty success). Optional nameFilter (ordinal, case-insensitive substring; surrounding whitespace trimmed; same matching as find_references / find_callers nameFilter) keeps only diagnostics whose diagnostic site — the same site accessibilityFilter / kindFilter judge — has a simple name (Roslyn ISymbol.Name: accessors get_X / set_X, constructors .ctor) containing it, e.g. Handle for diagnostics inside HandleOrder; namespace-level sites and diagnostics with no source location never match; combined with the other filters and applied before maxResults (omit / blank = no name filter; no match → empty success). Optional maxResults caps the returned list (omit = no cap).";

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
                description = "Absolute path to a .cs file to restrict diagnostics to (optional)"
            },
            severityFilter = new
            {
                type = "string",
                description = "Minimum severity: Error, Warning (default), Info, Hidden, or All",
                @default = "Warning"
            },
            maxResults = new
            {
                type = "integer",
                description = "Maximum number of diagnostics to return (optional; omit = no cap)",
                minimum = 1
            },
            diagnosticIds = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Diagnostic IDs to restrict results to, e.g. [\"CS0168\", \"CS8019\"] (optional; case-insensitive; omit or empty = all IDs). Applied before maxResults."
            },
            excludeDiagnosticIds = new
            {
                type = "array",
                items = new { type = "string" },
                description = "Diagnostic IDs to drop from results, e.g. [\"CS1591\", \"CS8019\"] (optional; case-insensitive; omit or empty = exclude none). Exclusion wins when an ID is also in diagnosticIds. Applied before maxResults."
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict diagnostics to (optional; omit = whole solution). Only that project is compiled and scanned (all target frameworks of a multi-targeted project). Combined with sourceFile and the ID / severity filters, applied before maxResults. A path matching no project → SourceNotInWorkspace."
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict diagnostics to (optional; omit for all namespaces). Keeps diagnostics whose diagnostic site — the member, type, or namespace declaration enclosing the diagnostic location — lives in a namespace that equals it or is nested inside it, e.g. App.Services matches App.Services.Orders but not App.ServicesExtra; ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace sites and diagnostics with no source location never match a named filter. Same matching as find_references / find_callers / search_symbols namespaceFilter; combined with sourceFile/projectPath/severityFilter/diagnosticIds/excludeDiagnosticIds/accessibilityFilter/messageFilter/kindFilter/nameFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Declared accessibility to restrict diagnostics to (optional; omit for all accessibilities): public, internal, protected, private, protected internal, or private protected (case-insensitive; same values as search_symbols / find_references accessibilityFilter; whitespace trimmed/collapsed; internal protected / protected private also accepted). Keeps diagnostics whose diagnostic site — the nearest declared member at a declaration-attached location (e.g. CS0169 on a field), otherwise the member / type enclosing the location (lambdas and local functions looked through to their declaring member; namespace-level sites never match) — has a DeclaredAccessibility equal to it exactly (implicit defaults count: a class member with no modifier is private; protected internal is not matched by protected or internal). Diagnostics with no source location never match. Combined with sourceFile/projectPath/severityFilter/diagnosticIds/excludeDiagnosticIds/namespaceFilter/messageFilter/kindFilter/nameFilter and applied before maxResults so totalCount/truncated reflect the filtered set; invalid value → InvalidVisibility"
            },
            messageFilter = new
            {
                type = "string",
                description = "Keep only diagnostics whose message contains this value (optional; ordinal, case-insensitive substring on the same text returned in each diagnostic's message; surrounding whitespace trimmed). Same matching as get_document_outline / find_references nameFilter. Diagnostics with no source location are kept when their message matches. Combined with sourceFile/projectPath/severityFilter/diagnosticIds/excludeDiagnosticIds/namespaceFilter/accessibilityFilter/kindFilter/nameFilter (a diagnostic must satisfy all that are set) and applied before maxResults so totalCount/truncated reflect the filtered set; omit, empty, or whitespace = no message filter"
            },
            kindFilter = new
            {
                type = "string",
                description = "Symbol kind to restrict diagnostics to (optional; omit for all kinds): a single SymbolKind name — Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (case-insensitive; same values and parsing as search_symbols / find_references / find_callers kindFilter; whitespace trimmed; numeric / comma-combined values rejected). Keeps diagnostics whose diagnostic site — the nearest declared member at a declaration-attached location (e.g. CS0169 on a field), otherwise the member / type enclosing the location (lambdas and local functions looked through to their declaring member; methods / constructors / accessors → Method, including an expression-bodied property / indexer body such as int P => Missing (its getter); fields → Field or Constant; property declarations → Property; namespace-level sites never match, including Namespace) — has that kind via SymbolKindMapper. Diagnostics with no source location never match. Combined with sourceFile/projectPath/severityFilter/diagnosticIds/excludeDiagnosticIds/namespaceFilter/accessibilityFilter/messageFilter/nameFilter (a diagnostic must satisfy all that are set) and applied before maxResults so totalCount/truncated reflect the filtered set; invalid value → InvalidSymbolKind; no match → empty success"
            },
            nameFilter = new
            {
                type = "string",
                description = "Name substring to restrict diagnostics to (optional; omit, empty, or whitespace for all names). Keeps diagnostics whose diagnostic site — the same site accessibilityFilter / kindFilter judge: the nearest declared member at a declaration-attached location (e.g. CS0169 on a field), otherwise the member / type enclosing the location (lambdas and local functions looked through to their declaring member) — has a simple name containing it (ordinal, case-insensitive; surrounding whitespace trimmed; inner whitespace not collapsed), e.g. Handle for diagnostics inside HandleOrder or OrderHandler. The name is Roslyn's ISymbol.Name, so accessors read get_X/set_X (including an expression-bodied property's implicit getter) and constructors .ctor. Namespace-level sites (unused usings, top-level statements) and diagnostics with no source location never match. Same matching as find_references / find_callers / get_document_outline nameFilter; combined with sourceFile/projectPath/severityFilter/diagnosticIds/excludeDiagnosticIds/namespaceFilter/accessibilityFilter/messageFilter/kindFilter (a diagnostic must satisfy all that are set) and applied before maxResults so totalCount/truncated reflect the filtered set; no match → empty success"
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

            var args = JsonSerializer.Deserialize<GetDiagnosticsArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new GetDiagnosticsOperation(context);
            var @params = new GetDiagnosticsParams
            {
                SourceFile = args.SourceFile,
                SeverityFilter = args.SeverityFilter,
                MaxResults = args.MaxResults,
                DiagnosticIds = args.DiagnosticIds,
                ExcludeDiagnosticIds = args.ExcludeDiagnosticIds,
                ProjectPath = args.ProjectPath,
                NamespaceFilter = args.NamespaceFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                MessageFilter = args.MessageFilter,
                KindFilter = args.KindFilter,
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

    private sealed class GetDiagnosticsArgs
    {
        public string SolutionPath { get; init; } = "";
        public string? SourceFile { get; init; }
        public string? SeverityFilter { get; init; }
        public int? MaxResults { get; init; }
        public List<string>? DiagnosticIds { get; init; }
        public List<string>? ExcludeDiagnosticIds { get; init; }
        public string? ProjectPath { get; init; }
        public string? NamespaceFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? MessageFilter { get; init; }
        public string? KindFilter { get; init; }
        public string? NameFilter { get; init; }
    }
}

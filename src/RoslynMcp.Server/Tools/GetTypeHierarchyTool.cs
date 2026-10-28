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
    public string Description => "Get the type hierarchy for a C# type: base types (ancestors), derived types (descendants), and implemented interfaces. Optional maxResults caps the returned DerivedTypes list (omit = no cap). BaseTypes and Interfaces are not capped. sourceFile locates the target type; optional derivedFile restricts reported DerivedTypes to those declared in that file (applied before maxResults, so totalCount/truncated reflect the filtered set; BaseTypes and Interfaces are not filtered); optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported DerivedTypes to those declared in documents of that project, combined with derivedFile and applied before maxResults (omit = whole solution); optional transitive (default true) set to false returns only direct subclasses (types whose immediate base type is the target) instead of every descendant, with derivedFile/projectPath/maxResults applied to that direct set (BaseTypes and Interfaces unaffected); optional namespaceFilter restricts reported DerivedTypes to those whose containing namespace equals it or is nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as search_symbols / find_implementations namespaceFilter), combined with derivedFile/projectPath/transitive and applied before maxResults (BaseTypes and Interfaces are not filtered); optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info / find_implementations accessibilityFilter) keeps only DerivedTypes whose DeclaredAccessibility equals it exactly (implicit defaults count: a top-level type with no modifier is internal, a nested type with no modifier is private), combined with the other filters and applied before maxResults (BaseTypes and Interfaces are not filtered); optional kindFilter (a single SymbolKind name such as Class or Record; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info / find_implementations kindFilter) keeps only DerivedTypes of that kind (records are Record, not Class, even though their entry Kind reads Class), combined with the other filters and applied before maxResults (BaseTypes and Interfaces are not filtered); optional nameFilter (ordinal, case-insensitive substring; surrounding whitespace trimmed; same matching as get_document_outline / get_symbol_info / find_implementations nameFilter) keeps only DerivedTypes whose simple name contains it, combined with the other filters and applied before maxResults (BaseTypes and Interfaces are not filtered); optional abstractFilter (boolean) set to true keeps only abstract DerivedTypes (INamedTypeSymbol.IsAbstract: abstract classes and abstract records) and false keeps only concrete, instantiable ones (including sealed classes and records), combined with the other filters and applied before maxResults (with transitive false it filters the direct subclasses only; BaseTypes and Interfaces are not filtered; a non-boolean value fails with INTERNAL_ERROR).";

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
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict reported DerivedTypes to (optional; omit for all namespaces). Keeps derived types whose containing namespace equals it or is nested inside it, e.g. App.Services matches App.Services.Orders but not App.ServicesExtra; ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace types never match a named filter. Same matching as search_symbols namespaceFilter; combined with derivedFile/projectPath/transitive/accessibilityFilter/kindFilter/nameFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; does not filter BaseTypes or Interfaces; sourceFile may be in another namespace"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Declared accessibility to restrict reported DerivedTypes to (optional; omit for all accessibilities): public, internal, protected, private, protected internal, or private protected (case-insensitive; surrounding whitespace trimmed and inner whitespace runs collapsed; internal protected / protected private also accepted). Keeps derived types whose DeclaredAccessibility equals it exactly (implicit defaults count: a top-level type with no modifier is internal, a nested type with no modifier is private; protected internal is not matched by protected or internal). Same values and InvalidVisibility error as search_symbols / get_document_outline / get_symbol_info / find_implementations accessibilityFilter; combined with derivedFile/projectPath/transitive/namespaceFilter/kindFilter/nameFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; does not filter BaseTypes or Interfaces"
            },
            kindFilter = new
            {
                type = "string",
                description = "Symbol kind to restrict reported DerivedTypes to (optional; omit for all kinds): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; case-insensitive; surrounding whitespace trimmed; numeric or comma-combined values are rejected with InvalidSymbolKind, even when direction is Ancestors). Keeps derived types whose shared SymbolKind classification equals it (same as find_implementations Kind: records are Record, not Class, even though a record's TypeHierarchyEntry kind reads Class); since classes derive only from classes and records only from records, the derived types of one target are typically all Class or all Record. Same values and parsing as search_symbols / get_document_outline / get_symbol_info / find_implementations kindFilter; combined with derivedFile/projectPath/transitive/namespaceFilter/accessibilityFilter/nameFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; a kind no derived type has returns an empty success; does not filter BaseTypes or Interfaces"
            },
            nameFilter = new
            {
                type = "string",
                description = "Name substring to restrict reported DerivedTypes to (optional; omit, empty, or whitespace for all names). Keeps derived types whose simple name (TypeHierarchyEntry typeName) contains it (ordinal, case-insensitive; surrounding whitespace trimmed; inner whitespace not collapsed), e.g. Admin for the AdminController / SuperAdminController subclasses of ControllerBase. Same matching as get_document_outline / get_symbol_info / find_implementations nameFilter; combined with derivedFile/projectPath/transitive/namespaceFilter/accessibilityFilter/kindFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; no match returns an empty success; does not filter BaseTypes or Interfaces"
            },
            abstractFilter = new
            {
                type = "boolean",
                description = "Restrict reported DerivedTypes by abstract-ness (optional; omit for both): true keeps only derived types whose INamedTypeSymbol.IsAbstract is true (abstract classes and abstract records, e.g. intermediate bases), false keeps only concrete, instantiable derived types (including sealed classes and records). Derived types are always classes or records, judged on their own abstract modifier (a partial type is abstract when any part declares it). Combined with derivedFile/projectPath/transitive/namespaceFilter/accessibilityFilter/kindFilter/nameFilter and applied before maxResults so totalCount/truncated reflect the filtered set; with transitive false it filters the direct subclasses only; does not filter BaseTypes or Interfaces and has no effect when direction is Ancestors; a non-boolean value fails with INTERNAL_ERROR"
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
                Transitive = args.Transitive,
                NamespaceFilter = args.NamespaceFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                KindFilter = args.KindFilter,
                NameFilter = args.NameFilter,
                AbstractFilter = args.AbstractFilter
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
        public string? NamespaceFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? KindFilter { get; init; }
        public string? NameFilter { get; init; }
        public bool? AbstractFilter { get; init; }
    }
}

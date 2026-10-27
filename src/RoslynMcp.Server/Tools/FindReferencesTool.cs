using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for find_references query.
/// </summary>
public sealed class FindReferencesTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new find references tool.
    /// </summary>
    public FindReferencesTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "find_references";

    /// <inheritdoc />
    public string Description => "Find all references to a C# symbol across the solution. Returns file locations, context snippets, and whether each reference is a definition or usage. Optional includeDeclaration (default true) set to false returns usages only, excluding the symbol's own definition locations before maxResults is applied. sourceFile locates the target symbol; optional referenceFile restricts reported locations to those in that file (definition locations included only when they are in that file; applied before maxResults, so totalCount/truncated reflect the filtered set). Optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported locations to documents in that project, applied before maxResults. Optional namespaceFilter restricts reported locations to reference sites (the code enclosing each location, not the target symbol) whose containing namespace equals it or is nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as search_symbols namespaceFilter), combined with the other filters and applied before maxResults. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_callers accessibilityFilter) keeps only locations whose reference site's enclosing symbol (the member or type enclosing each usage, not the target symbol; definition locations are judged by the declared symbol itself) has a DeclaredAccessibility equal to it exactly (implicit defaults count: a class member with no modifier is private), combined with the other filters and applied before maxResults. Optional nameFilter (ordinal, case-insensitive substring; surrounding whitespace trimmed; same matching as get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy nameFilter) keeps only locations whose reference site's enclosing symbol (the same site symbol accessibilityFilter judges: the member or type enclosing each usage, not the target symbol; definition locations are judged by the declared symbol itself; namespace-level sites never match) has a simple name containing it, combined with the other filters and applied before maxResults. Optional kindFilter (a single kind name such as Method, Property, Field, Event, Constant; case-insensitive; same values and InvalidSymbolKind error as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_callers kindFilter) keeps only locations whose reference site's enclosing symbol (the same site symbol accessibilityFilter / nameFilter judge) has that kind (methods, constructors, and property/event accessors are Method; a field initializer is Field; a property definition is Property; top-level statements, enclosed by the synthesized <Main>$, are namespace-level sites and never match any kind, including Method; a site inside a delegate's Invoke/BeginInvoke/EndInvoke/.ctor is classified as the delegate itself, so it matches Delegate, not Method), combined with the other filters and applied before maxResults. Optional staticFilter (boolean) keeps only locations whose reference site (the same enclosing site symbol accessibilityFilter / nameFilter / kindFilter judge; definition locations judged by the declared symbol itself) is static when true (ISymbol.IsStatic: static methods, static constructors, operators, static properties / indexers / events and their accessors, static and const fields, static types) or instance when false; same IsStatic test as get_symbol_info / get_document_outline / search_symbols / find_callers staticFilter, and the target symbol's own staticness is irrelevant. Lambdas (including static lambdas) and local functions (including static local functions) are judged on the member declaring them (unlike find_callers, where a local function is its own calling symbol); a site inside a delegate's synthesized members is judged on the delegate type (never static); namespace-level sites never match either value. Combined with the other filters and applied before maxResults so totalCount/truncated reflect the filtered set; a non-boolean value is rejected with INTERNAL_ERROR.";

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
                description = "Name of the symbol to find references for"
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
                description = "Maximum number of references to return",
                minimum = 1
            },
            includeDeclaration = new
            {
                type = "boolean",
                description = "Include the symbol's own definition locations (default true). false returns usages only; applied before maxResults so totalCount/truncated reflect usages only"
            },
            referenceFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict reported reference locations to those located in that file, including definition locations when includeDeclaration is on (optional; omit for the whole solution)"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict reported reference locations to those whose document belongs to that project, including definition locations when includeDeclaration is on (optional; omit for the whole solution). Every target framework of a multi-targeted project is included; combined with referenceFile and applied before maxResults so totalCount/truncated reflect the project-scoped set; sourceFile may be in another project; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict reported reference locations to (optional; omit for all namespaces). Keeps locations whose reference site (the member, type, or namespace declaration enclosing the location; not the target symbol's namespace), including definition locations when includeDeclaration is on, lives in a namespace that equals it or is nested inside it, e.g. App.Services matches App.Services.Orders but not App.ServicesExtra; ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace sites (top-level statements, usings outside any namespace) never match a named filter. Same matching as search_symbols namespaceFilter; combined with referenceFile/projectPath/includeDeclaration/accessibilityFilter/nameFilter/kindFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Declared accessibility to restrict reported reference locations to (optional; omit for all accessibilities): public, internal, protected, private, protected internal, or private protected (case-insensitive; surrounding whitespace trimmed and inner whitespace runs collapsed; internal protected / protected private also accepted). Keeps usage locations whose reference site's enclosing symbol (the member, type, or namespace declaration enclosing the location, the same site notion as namespaceFilter; not the target symbol; lambdas and local functions are looked through to their declaring member; namespace-level sites never match) has a DeclaredAccessibility equal to it exactly (implicit defaults count: a class member with no modifier is private; protected internal is not matched by protected or internal). Definition locations, when includeDeclaration is on, are judged by the declared symbol itself (an internal method on a public type is internal), or for a local / parameter / local function by the member declaring it. Same values and InvalidVisibility error as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_callers accessibilityFilter; combined with referenceFile/projectPath/namespaceFilter/nameFilter/kindFilter/includeDeclaration and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            nameFilter = new
            {
                type = "string",
                description = "Name substring to restrict reported reference locations to (optional; omit, empty, or whitespace for all names). Keeps locations whose reference site's enclosing symbol (the same site symbol accessibilityFilter judges: the member or type enclosing the location, not the target symbol; lambdas and local functions are looked through to their declaring member) has a simple name containing it (ordinal, case-insensitive; surrounding whitespace trimmed; inner whitespace not collapsed), e.g. Handle for usages inside HandleOrder or OrderHandler. The name is Roslyn's ISymbol.Name, so accessors read get_X/set_X, constructors .ctor. Definition locations, when includeDeclaration is on, are judged by the declared symbol itself, or for a local / parameter / local function by the member declaring it; namespace-level sites (using aliases, a top-level type's base list, top-level statements) never match. Same matching as get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy nameFilter; combined with referenceFile/projectPath/namespaceFilter/accessibilityFilter/kindFilter/includeDeclaration and applied before maxResults so totalCount/truncated reflect the filtered set; no match returns an empty success"
            },
            kindFilter = new
            {
                type = "string",
                description = "Symbol kind to restrict reported reference locations to (optional; omit for all kinds): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; case-insensitive; surrounding whitespace trimmed; numeric or comma-combined values are rejected with InvalidSymbolKind). Keeps locations whose reference site's enclosing symbol (the same site symbol accessibilityFilter / nameFilter judge: the member or type enclosing the location, not the target symbol; lambdas and local functions are looked through to their declaring member) has that kind via SymbolKindMapper: methods, constructors, operators, and property/event accessors are Method; a field initializer is Field (Constant for a const); a property definition site is Property, e.g. Method for usages inside methods and accessors. Definition locations, when includeDeclaration is on, are judged by the declared symbol itself, or for a local / parameter / local function by the member declaring it; namespace-level sites never match a named kind filter: top-level statements, enclosed by the synthesized <Main>$, are namespace-level and never match any kind, including Method. A site inside a delegate's Invoke/BeginInvoke/EndInvoke/.ctor is classified as the delegate itself, so it matches Delegate, not Method. Same values and parsing as search_symbols / get_document_outline / get_symbol_info / find_implementations / get_type_hierarchy / find_callers kindFilter; combined with referenceFile/projectPath/namespaceFilter/accessibilityFilter/nameFilter/includeDeclaration and applied before maxResults so totalCount/truncated reflect the filtered set; a kind no site has returns an empty success"
            },
            staticFilter = new
            {
                type = "boolean",
                description = "Keep only reference locations whose reference site (the same enclosing site symbol accessibilityFilter / nameFilter / kindFilter judge; a definition location is judged by the declared symbol itself) is static when true (ISymbol.IsStatic: static methods, static constructors, operators, static properties / indexers / events and their accessors, static and const fields — Roslyn reports const as static — and static types) or instance when false (optional; omit = no static filter). Same IsStatic test as get_symbol_info / get_document_outline / search_symbols / find_callers staticFilter; the target symbol's own staticness is irrelevant. Lambdas (including static lambdas) and local functions (including static local functions) are judged on the member declaring them (unlike find_callers, where a local function is its own calling symbol); a site inside a delegate's synthesized members is judged on the delegate type (never static); namespace-level sites (using aliases, a top-level type's base list, top-level statements, a namespace's own declaration) never match either value. Combined with referenceFile/projectPath/namespaceFilter/accessibilityFilter/nameFilter/kindFilter/includeDeclaration and applied before maxResults so totalCount/truncated reflect the filtered set; no match → empty references, totalCount = 0, truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR."
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

            var args = JsonSerializer.Deserialize<FindReferencesArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new FindReferencesOperation(context);
            var @params = new FindReferencesParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                MaxResults = args.MaxResults,
                IncludeDeclaration = args.IncludeDeclaration,
                ReferenceFile = args.ReferenceFile,
                ProjectPath = args.ProjectPath,
                NamespaceFilter = args.NamespaceFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                NameFilter = args.NameFilter,
                KindFilter = args.KindFilter,
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

    private sealed class FindReferencesArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? MaxResults { get; init; }
        public bool? IncludeDeclaration { get; init; }
        public string? ReferenceFile { get; init; }
        public string? ProjectPath { get; init; }
        public string? NamespaceFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? NameFilter { get; init; }
        public string? KindFilter { get; init; }
        public bool? StaticFilter { get; init; }
    }
}

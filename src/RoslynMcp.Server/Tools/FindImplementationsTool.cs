using System.Text.Json;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using RoslynMcp.Server.Transport;

namespace RoslynMcp.Server.Tools;

/// <summary>
/// MCP tool handler for find_implementations query.
/// </summary>
public sealed class FindImplementationsTool : IToolHandler
{
    private readonly IWorkspaceProvider _workspaceProvider;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Creates a new find implementations tool.
    /// </summary>
    public FindImplementationsTool(IWorkspaceProvider workspaceProvider)
    {
        _workspaceProvider = workspaceProvider;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public string Name => "find_implementations";

    /// <inheritdoc />
    public string Description => "Find all implementations of a C# interface, abstract class, or virtual/abstract member across the solution. sourceFile locates the target symbol; optional implementationFile restricts reported implementations to those declared in that file (applied before maxResults, so totalCount/truncated reflect the filtered set). Optional projectPath (absolute .csproj path of a project in the loaded solution) restricts reported implementations to those declared in documents of that project, applied before maxResults. Optional transitive (default true) set to false on an interface returns only types that implement it directly (not those inheriting the implementation from a base class or implementing it via a derived interface), with implementationFile/projectPath/maxResults applied to that direct set; no effect for member targets. Optional namespaceFilter restricts reported implementations to those whose containing namespace equals it or is nested inside it (ordinal; e.g. App.Services matches App.Services.Orders, not App.ServicesExtra; same matching as search_symbols namespaceFilter), combined with the other filters and applied before maxResults. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info accessibilityFilter) keeps only implementations whose DeclaredAccessibility equals it exactly (implicit defaults count: a top-level type with no modifier is internal, a nested type with no modifier is private; for member targets the implementing member's accessibility is used), combined with the other filters and applied before maxResults. Optional kindFilter (a single SymbolKind name such as Class, Struct, Interface, Record, Method, Property, Event; case-insensitive; same values as search_symbols / get_document_outline / get_symbol_info kindFilter) keeps only implementations whose reported Kind equals it (records are Record, not Class), combined with the other filters and applied before maxResults. Optional nameFilter (ordinal, case-insensitive substring; same matching as get_document_outline / get_symbol_info nameFilter) keeps only implementations whose reported Name contains it (for member targets the implementing member's own name, so it is mainly useful for type targets), combined with the other filters and applied before maxResults. Optional staticFilter (boolean; same ISymbol.IsStatic test as get_symbol_info / get_document_outline / search_symbols / find_callers / find_references / get_diagnostics staticFilter) keeps only static (true) or only instance (false) implementations, e.g. the static implementations of a static abstract interface member. It judges each returned implementation symbol itself, unlike find_references / find_callers / get_diagnostics staticFilter, which judge the enclosing site symbol of each usage / call / diagnostic. Implementing types are never static, so for type targets true returns an empty success while false or omitted keeps every implementing type. C# requires an implementing member's staticness to equal its target's, so for a member target the filter is all-or-nothing (the target's staticness decides). Explicit interface implementations (static or instance) are judged on their own IsStatic; a default interface member re-implemented explicitly in a derived interface is returned and judged on that re-implementation (the derived interface itself is not returned for a type target); records, record structs, structs, nested types, partial members (reported once, at the defining declaration), generic implementers, and implementers of a self-referencing generic static abstract member (ISelf<TSelf>) are judged like any other. Implementations in other projects of the solution are returned and filtered; implementations declared only in metadata are never listed (for a metadata target they still count toward totalCount, so truncated can be true). Current behavior: static-class targets, abstract or virtual class targets, and abstract / virtual class members with overrides (including a generic base such as GB<T>.GM) return no implementations even without a filter. Combined with the other filters and applied before maxResults; a non-boolean value fails with INTERNAL_ERROR. Optional abstractFilter (boolean): true keeps only implementations whose ISymbol.IsAbstract is true, false keeps only non-abstract ones. Judges each returned implementation symbol itself (same IsAbstract test as get_type_hierarchy abstractFilter). For an interface type target, implementing types are judged: abstract classes count as abstract even with no abstract members (including abstract records, nested and generic abstract classes, and abstract classes implementing the interface only explicitly); sealed classes, structs, classes with only virtual or extern members, and concrete subclasses of an abstract implementer are not. No pruning: with transitive true (default) concrete subclasses of a filtered-out abstract implementer are still listed for false (with transitive false they are dropped because they do not list the interface directly); derived interfaces are never returned for a type target. For an interface member target (method, property, event), implementing members are judged on their own IsAbstract, independent of their containing type: abstract members and an abstract re-abstraction in a derived interface (abstract void IRunner.Run();) are abstract; a member with a body in an abstract class, explicit implementations, a default-body re-implementation in a derived interface, and virtual members are not; overrides of an abstract implementing member are never listed (current behavior). Implementations of a static abstract interface member are static and never abstract, so true returns an empty success for that member target (the implementing type is still judged on its own IsAbstract for a type target). Implementations in other projects are returned and filtered; metadata-only implementations are never listed (for a metadata target such as System.IDisposable they are still judged and count toward totalCount, so truncated can be true). Current behavior: abstract or virtual class targets and abstract / virtual class members with overrides return nothing even without a filter. A derived-interface target (e.g. IFastRunner : IRunner) is an interface target like any other: its implementing types are returned and judged the same way. Combined with the other filters (including staticFilter) and applied before maxResults so totalCount/truncated reflect the filtered set; a non-boolean value fails with INTERNAL_ERROR.";

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
                description = "Absolute path to the source file containing the interface or abstract symbol"
            },
            symbolName = new
            {
                type = "string",
                description = "Name of the interface, abstract class, or virtual member"
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
                description = "Maximum number of implementations to return",
                minimum = 1
            },
            implementationFile = new
            {
                type = "string",
                description = "Absolute path to a .cs file to restrict reported implementations to those declared in that file (optional; omit for the whole solution)"
            },
            projectPath = new
            {
                type = "string",
                description = "Absolute path to a .csproj in the loaded solution to restrict reported implementations to those declared in a document of that project (optional; omit for the whole solution). Every target framework of a multi-targeted project is included; combined with implementationFile and applied before maxResults so totalCount/truncated reflect the project-scoped set; sourceFile may be in another project; a relative or non-.csproj path is rejected and a path matching no project in the workspace returns a source-not-in-workspace error"
            },
            transitive = new
            {
                type = "boolean",
                description = "When the target is an interface, include every implementing type (default true). false returns only types that implement the interface directly (not those inheriting the implementation from a base class or implementing it via a derived interface); implementationFile/projectPath filter and maxResults cap that direct set, so totalCount/truncated reflect it. No effect for member targets or non-interface types"
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Namespace to restrict reported implementations to (optional; omit for all namespaces). Keeps implementations whose containing namespace (for member targets, that of the member's containing type) equals it or is nested inside it, e.g. App.Services matches App.Services.Orders but not App.ServicesExtra; ordinal (case-sensitive); surrounding whitespace trimmed; a leading global:: is accepted and a bare global:: filters nothing; global-namespace implementations never match a named filter. Same matching as search_symbols namespaceFilter; combined with implementationFile/projectPath/transitive/accessibilityFilter/kindFilter/nameFilter/staticFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Declared accessibility to restrict reported implementations to (optional; omit for all accessibilities): public, internal, protected, private, protected internal, or private protected (case-insensitive; surrounding whitespace trimmed and inner whitespace runs collapsed; internal protected / protected private also accepted). Keeps implementations whose DeclaredAccessibility equals it exactly (implicit defaults count: a top-level type with no modifier is internal, a nested type with no modifier is private; protected internal is not matched by protected or internal); for member targets the implementing member's declared accessibility is used (explicit interface implementations are private). Same values and InvalidVisibility error as search_symbols / get_document_outline / get_symbol_info accessibilityFilter; combined with implementationFile/projectPath/transitive/namespaceFilter/kindFilter/nameFilter/staticFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set"
            },
            kindFilter = new
            {
                type = "string",
                description = "Symbol kind to restrict reported implementations to (optional; omit for all kinds): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; case-insensitive; surrounding whitespace trimmed; numeric or comma-combined values are rejected with InvalidSymbolKind). Keeps implementations whose reported Kind equals it (records are Record, not Class), e.g. Struct for the structs implementing an interface or Property for property implementations of a member target. Same values and parsing as search_symbols / get_document_outline / get_symbol_info kindFilter; combined with implementationFile/projectPath/transitive/namespaceFilter/accessibilityFilter/nameFilter/staticFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; a kind no implementation has returns an empty success"
            },
            nameFilter = new
            {
                type = "string",
                description = "Name substring to restrict reported implementations to (optional; omit, empty, or whitespace for all names). Keeps implementations whose reported Name contains it (ordinal, case-insensitive; surrounding whitespace trimmed), e.g. Sql for the SqlOrderRepository / SqlUserRepository implementers of IRepository. For member targets the implementing member's own name is used (overrides usually share the target's name; explicit interface implementations report the qualified name such as App.IRunner.Run), so it is mainly useful for type targets. Same matching as get_document_outline / get_symbol_info nameFilter; combined with implementationFile/projectPath/transitive/namespaceFilter/accessibilityFilter/kindFilter/staticFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; no match returns an empty success"
            },
            staticFilter = new
            {
                type = "boolean",
                description = "Restrict reported implementations by static-ness (optional; omit for both): true keeps only implementations whose ISymbol.IsStatic is true (e.g. static implementations of a static abstract / static virtual interface member), false keeps only instance implementations. Judges each returned implementation symbol itself, unlike find_references / find_callers / get_diagnostics staticFilter (which judge the enclosing site symbol of each usage / call / diagnostic). Implementing types are never static, so for type targets true returns an empty success while false or omitted keeps every implementing type. An implementing member's staticness always equals its target's, so for a member target the filter is all-or-nothing (the target's staticness decides). Explicit implementations (static or instance) and default interface members re-implemented in a derived interface are judged on their own IsStatic; records, record structs, structs, nested types, partial members (reported once), generic implementers, and self-referencing generic static abstract implementers (ISelf<TSelf>) are judged like any other. Implementations in other projects are returned; metadata-only implementations are never listed (for a metadata target they still count toward totalCount). Current behavior: static-class targets, abstract or virtual class targets, and abstract / virtual class members with overrides return nothing even without a filter. Same IsStatic test as get_symbol_info / get_document_outline / search_symbols / find_callers / find_references / get_diagnostics staticFilter; combined with implementationFile/projectPath/transitive/namespaceFilter/accessibilityFilter/kindFilter/nameFilter/abstractFilter and applied before maxResults so totalCount/truncated reflect the filtered set; a non-boolean value fails with INTERNAL_ERROR"
            },
            abstractFilter = new
            {
                type = "boolean",
                description = "Restrict reported implementations by abstractness (optional; omit for both): true keeps only implementations whose ISymbol.IsAbstract is true, false keeps only non-abstract ones. Judges each returned implementation symbol itself (same IsAbstract test as get_type_hierarchy abstractFilter). For an interface type target, implementing types are judged: abstract classes count as abstract even with no abstract members (including abstract records, nested and generic abstract classes, and abstract classes implementing the interface only explicitly); sealed classes, structs, classes with only virtual or extern members, and concrete subclasses of an abstract implementer are not. No pruning: with transitive true (default) concrete subclasses of a filtered-out abstract implementer are still listed for false (with transitive false they are dropped because they do not list the interface directly); derived interfaces are never returned for a type target. For an interface member target (method, property, event), implementing members are judged on their own IsAbstract, independent of their containing type: abstract members and an abstract re-abstraction in a derived interface (abstract void IRunner.Run();) are abstract; a member with a body in an abstract class, explicit implementations, a default-body re-implementation in a derived interface, and virtual members are not; overrides of an abstract implementing member are never listed (current behavior). Implementations of a static abstract interface member are static and never abstract, so true returns an empty success for that member target (the implementing type is still judged on its own IsAbstract for a type target). Implementations in other projects are returned and filtered; metadata-only implementations are never listed (for a metadata target such as System.IDisposable they are still judged and count toward totalCount, so truncated can be true). Current behavior: abstract or virtual class targets and abstract / virtual class members with overrides return nothing even without a filter. A derived-interface target (e.g. IFastRunner : IRunner) is an interface target like any other: its implementing types are returned and judged the same way. Combined with implementationFile/projectPath/transitive/namespaceFilter/accessibilityFilter/kindFilter/nameFilter/staticFilter and applied before maxResults so totalCount/truncated reflect the filtered set; a non-boolean value fails with INTERNAL_ERROR"
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

            var args = JsonSerializer.Deserialize<FindImplementationsArgs>(arguments.Value.GetRawText(), _jsonOptions);
            if (args == null)
                return ToolResult.Error("Failed to parse arguments");

            using var context = await _workspaceProvider.CreateContextAsync(args.SolutionPath, cancellationToken);

            var operation = new FindImplementationsOperation(context);
            var @params = new FindImplementationsParams
            {
                SourceFile = args.SourceFile,
                SymbolName = args.SymbolName,
                Line = args.Line,
                Column = args.Column,
                MaxResults = args.MaxResults,
                ImplementationFile = args.ImplementationFile,
                ProjectPath = args.ProjectPath,
                Transitive = args.Transitive,
                NamespaceFilter = args.NamespaceFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                KindFilter = args.KindFilter,
                NameFilter = args.NameFilter,
                StaticFilter = args.StaticFilter,
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

    private sealed class FindImplementationsArgs
    {
        public string SolutionPath { get; init; } = "";
        public string SourceFile { get; init; } = "";
        public string? SymbolName { get; init; }
        public int? Line { get; init; }
        public int? Column { get; init; }
        public int? MaxResults { get; init; }
        public string? ImplementationFile { get; init; }
        public string? ProjectPath { get; init; }
        public bool? Transitive { get; init; }
        public string? NamespaceFilter { get; init; }
        public string? AccessibilityFilter { get; init; }
        public string? KindFilter { get; init; }
        public string? NameFilter { get; init; }
        public bool? StaticFilter { get; init; }
        public bool? AbstractFilter { get; init; }
    }
}

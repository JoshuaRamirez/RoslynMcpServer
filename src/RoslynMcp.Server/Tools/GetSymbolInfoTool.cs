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
    public string Description => "Get detailed information about a C# symbol including its type hierarchy, members, modifiers, parameters, return type, and XML documentation. Optional kindFilter (same values as search_symbols / get_document_outline kindFilter, case-insensitive) keeps only Members of that kind on a type, applied before maxResults so totalCount/truncated reflect the filtered set; Local/Parameter/Namespace never match a type member. Optional accessibilityFilter (public, internal, protected, private, protected internal, or private protected; case-insensitive; same values as search_symbols / get_document_outline accessibilityFilter) keeps only Members with exactly that declared accessibility, counting implicit defaults (a class member with no modifier is private); combined with kindFilter and applied before maxResults. Optional nameFilter (same matching as get_document_outline nameFilter: ordinal, case-insensitive substring on the member's simple name) keeps only Members whose name contains it; combined with kindFilter and accessibilityFilter and applied before maxResults. Optional namespaceFilter (same matching as search_symbols / get_document_outline / find_implementations / get_type_hierarchy / find_references / find_callers / get_diagnostics namespaceFilter: ordinal, case-sensitive; containing namespace equals it or is nested inside it; surrounding whitespace trimmed; leading global:: accepted; bare global:: filters nothing; global-namespace members never match) keeps only Members in that namespace scope; combined with kindFilter, accessibilityFilter, and nameFilter (order: kindFilter → accessibilityFilter → nameFilter → namespaceFilter → maxResults). Members of a type normally share the type's containing namespace (including nested types), so a named filter typically keeps all or none depending on whether the type's namespace matches or is nested under the filter (e.g. App keeps members of a type in App.Services). For metadata types, each member is judged by its own containing namespace. Optional maxResults caps the returned Members list of a type (omit = no cap); result includes TotalCount and Truncated. Optional staticFilter (boolean) keeps only static Members when true (const fields count as static) or only instance Members when false; omit = all members; applied after namespaceFilter and before maxResults. Nested types are judged by IsStatic too: a nested static class counts as static; any other nested type (class, struct, enum, interface, delegate, record) counts as instance. Operators (including conversion operators), constructors (including static constructors), indexers, property / event accessors and explicit interface implementations never appear in Members, so staticFilter = true cannot return them (a finalizer ~T() does appear and counts as instance). Like get_document_outline / search_symbols / find_implementations staticFilter, this judges each member's own symbol itself, unlike find_references / find_callers / get_diagnostics staticFilter, which judge the enclosing site symbol of each usage / call / diagnostic. No match → empty Members, TotalCount = 0, Truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR. Optional abstractFilter (boolean) keeps only abstract Members of a type symbol when true (ISymbol.IsAbstract: abstract / abstract override class members, interface members without a default body including static abstract members, nested abstract classes / abstract records and nested interfaces) or only non-abstract Members when false (members with bodies, virtual / override / sealed override, extern, fields, constants, default interface members, static virtual interface members with a body, nested concrete / sealed / static classes, structs, enums, delegates); omit = all members. Judged on each member's own symbol, independent of the target type: a concrete type's methods, properties, events and fields are never abstract, but its nested abstract classes / abstract records and nested interfaces still match true, so a concrete target is empty for true only when it declares no such nested types; a static class is static but not abstract. Abstract indexers, operators (including static abstract operators) and accessors never appear in Members, so abstractFilter = true cannot return them. The filter only affects Members: the target type itself is always returned whatever its own IsAbstract, so an abstract class with no abstract members gives empty Members for true, while a nested abstract class with no abstract members still matches true on its containing type. An abstract event matches true and a field-like event false. Metadata targets work too (e.g. System.IO.Stream): each metadata member is judged on its own IsAbstract (Read(byte[], int, int) / Write(byte[], int, int) / CanRead are abstract; CopyTo, Read(Span<byte>) and CanTimeout are not). Peer differences: get_symbol_info filters only one target's Members, never removes the target, and works on metadata targets; search_symbols abstractFilter judges every name-matched symbol, is source-only, and drops namespaces when the filter is set; get_type_hierarchy abstractFilter judges only derived types (including metadata) and returns empty for an interface root; find_implementations abstractFilter judges only implementers, counts metadata implementers in totalCount, and returns nothing for a class target. Combined with kindFilter, accessibilityFilter, nameFilter, namespaceFilter, and staticFilter (order: kindFilter → accessibilityFilter → nameFilter → namespaceFilter → staticFilter → abstractFilter → maxResults; e.g. staticFilter true + abstractFilter true keeps static abstract interface members) so totalCount/truncated reflect the filtered set; no effect on non-type symbols or on Interfaces/Parameters. No match → empty Members, TotalCount = 0, Truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR; CLI: bare --abstract-filter = true, a value other than true / false (case-insensitive) is rejected. Interfaces, Parameters and other fields are not filtered or capped, and kindFilter/accessibilityFilter/nameFilter/namespaceFilter/staticFilter/abstractFilter/maxResults have no effect on non-type symbols. Optional sealedFilter (boolean) keeps only Members of a type symbol whose ISymbol.IsSealed is true when true (sealed override methods / properties / events; nested sealed classes and sealed records; nested structs, including readonly structs and record structs; nested enums and delegates, which Roslyn reports as sealed) or only Members whose IsSealed is false when false (ordinary, virtual, abstract and non-sealed override members; fields, const fields and enum members; nested unsealed, abstract and static classes and records; nested interfaces; every interface member, including an explicitly sealed default-bodied one, which Roslyn reports as non-virtual rather than IsSealed; every member of a struct, including an override such as ToString). A finalizer is returned in Members (as ~T()), matches false and is excluded by true. Judged on each member's own symbol: the target type itself is always returned whatever its own IsSealed, so a sealed class or struct target with no sealed members gives empty Members for true. Never-returned kinds: indexers (so a sealed override indexer is never returned), operators and conversion operators, instance and static constructors, property / event accessors and explicit interface implementations (including explicit interface indexers) never appear in Members, so sealedFilter = true cannot return them. Metadata targets work too: each metadata member is judged on its own IsSealed (e.g. on System.Collections.Generic.List<int> the nested Enumerator struct matches true while Add matches false). Peer differences: get_symbol_info sealedFilter filters only one target's Members, never removes the target, and works on metadata targets; search_symbols sealedFilter judges every name-matched symbol, is source-only, and drops namespaces when the filter is set; get_document_outline, get_type_hierarchy and find_implementations have abstractFilter but no sealedFilter. Versus abstractFilter: no member is both abstract and sealed, so abstractFilter true + sealedFilter true is always empty; a static class is neither sealed nor abstract. Versus staticFilter: static members and static classes are never sealed, so staticFilter true + sealedFilter true is always empty; nested structs, enums and delegates are sealed but not static, so they match staticFilter false + sealedFilter true. Combined with kindFilter, accessibilityFilter, nameFilter, namespaceFilter, staticFilter and abstractFilter (all must match) and applied before maxResults so totalCount / truncated reflect the filtered set; no effect on non-type symbols, including namespace targets (Members stays null), or on Interfaces or Parameters. No match gives empty Members, totalCount = 0, truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR. CLI: bare --sealed-filter = true, and a value other than true/false (case-insensitive) is rejected. Omitted = no sealed filter.";

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
                description = "Maximum number of members to return for a type symbol (optional; omit = no cap). Applied after kindFilter, accessibilityFilter, nameFilter, namespaceFilter, staticFilter, abstractFilter, and sealedFilter so totalCount/truncated reflect the filtered set. Does not cap Interfaces or Parameters; no effect on non-type symbols.",
                minimum = 1
            },
            kindFilter = new
            {
                type = "string",
                description = "Keep only Members of this kind on a type symbol (optional; case-insensitive): Class, Struct, Interface, Enum, Record, Delegate, Method, Property, Field, Event, Constant, Local, Parameter, Namespace (a single kind name; numeric or comma-combined values are rejected with InvalidSymbolKind). Same values and parsing as search_symbols / get_document_outline kindFilter. Applied before maxResults so totalCount/truncated reflect the filtered set; Local/Parameter/Namespace never match a type member (empty Members); omit, empty, or whitespace = all members; no effect on non-type symbols or on Interfaces/Parameters"
            },
            accessibilityFilter = new
            {
                type = "string",
                description = "Keep only Members of a type symbol with this declared accessibility (optional; omit for any accessibility): public, internal, protected, private, protected internal, or private protected (case-insensitive, surrounding whitespace trimmed; internal protected / protected private also accepted). Same values and parsing as search_symbols / get_document_outline accessibilityFilter. Exact match on the member's declared accessibility, so implicit defaults count (a class member with no modifier is private, an interface or enum member is public) and protected internal is not matched by protected or internal; combined with kindFilter and nameFilter (a member must satisfy all that are set) and applied before maxResults so totalCount/truncated reflect the filtered set; no effect on non-type symbols or on Interfaces/Parameters; any other value returns an invalid-visibility error"
            },
            nameFilter = new
            {
                type = "string",
                description = "Keep only Members of a type symbol whose simple name contains this value (optional; ordinal, case-insensitive substring; surrounding whitespace trimmed). Same matching as get_document_outline nameFilter. Combined with kindFilter, accessibilityFilter, and namespaceFilter (a member must satisfy all that are set) and applied before maxResults so totalCount/truncated reflect the filtered set; omit, empty, or whitespace = all members; no effect on non-type symbols or on Interfaces/Parameters"
            },
            namespaceFilter = new
            {
                type = "string",
                description = "Keep only Members of a type symbol whose containing namespace equals this value or is nested inside it (optional; ordinal, case-sensitive; surrounding whitespace trimmed; leading global:: accepted; bare global:: filters nothing). Same matching as search_symbols / get_document_outline / find_implementations / get_type_hierarchy / find_references / find_callers / get_diagnostics namespaceFilter. Global-namespace members never match a named filter. Members of a type normally share the type's containing namespace (including nested types), so a named filter typically keeps all or none depending on whether the type's namespace matches or is nested under the filter (e.g. App keeps members of a type in App.Services). For metadata types, each member is judged by its own containing namespace. Combined with kindFilter, accessibilityFilter, and nameFilter (order: kindFilter → accessibilityFilter → nameFilter → namespaceFilter → maxResults) so totalCount/truncated reflect the filtered set; omit, empty, or whitespace = all members; no effect on non-type symbols or on Interfaces/Parameters"
            },
            staticFilter = new
            {
                type = "boolean",
                description = "Keep only static Members of a type symbol when true (ISymbol.IsStatic; const fields count as static) or only instance Members when false (optional; omit = all members). Combined with kindFilter, accessibilityFilter, nameFilter, and namespaceFilter (order: kindFilter → accessibilityFilter → nameFilter → namespaceFilter → staticFilter → maxResults) so totalCount/truncated reflect the filtered set; no effect on non-type symbols or on Interfaces/Parameters. Nested types are judged by IsStatic too: a nested static class counts as static; any other nested type (class, struct, enum, interface, delegate, record) counts as instance. Operators (including conversion operators), constructors (including static constructors), indexers, property / event accessors and explicit interface implementations never appear in Members, so staticFilter = true cannot return them (a finalizer ~T() does appear and counts as instance). Like get_document_outline / search_symbols / find_implementations staticFilter, this judges each member's own symbol itself, unlike find_references / find_callers / get_diagnostics staticFilter, which judge the enclosing site symbol of each usage / call / diagnostic. No match → empty Members, TotalCount = 0, Truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR."
            },
            abstractFilter = new
            {
                type = "boolean",
                description = "Keep only abstract Members of a type symbol when true (ISymbol.IsAbstract: abstract / abstract override class members, interface members without a default body including static abstract members, nested abstract classes / abstract records and nested interfaces) or only non-abstract Members when false (members with bodies, virtual / override / sealed override, extern, fields, constants, default interface members, static virtual interface members with a body, nested concrete / sealed / static classes, structs, enums, delegates) (optional; omit = all members). Judged on each member's own symbol, independent of the target type: a concrete type's methods, properties, events and fields are never abstract, but its nested abstract classes / abstract records and nested interfaces still match true, so a concrete target is empty for true only when it declares no such nested types; a static class is static but not abstract. Abstract indexers, operators (including static abstract operators) and accessors never appear in Members, so abstractFilter = true cannot return them. The filter only affects Members: the target type itself is always returned whatever its own IsAbstract, so an abstract class with no abstract members gives empty Members for true, while a nested abstract class with no abstract members still matches true on its containing type. An abstract event matches true and a field-like event false. Metadata targets work too (e.g. System.IO.Stream): each metadata member is judged on its own IsAbstract (Read(byte[], int, int) / Write(byte[], int, int) / CanRead are abstract; CopyTo, Read(Span<byte>) and CanTimeout are not). Peer differences: get_symbol_info filters only one target's Members, never removes the target, and works on metadata targets; search_symbols abstractFilter judges every name-matched symbol, is source-only, and drops namespaces when the filter is set; get_type_hierarchy abstractFilter judges only derived types (including metadata) and returns empty for an interface root; find_implementations abstractFilter judges only implementers, counts metadata implementers in totalCount, and returns nothing for a class target. Combined with kindFilter, accessibilityFilter, nameFilter, namespaceFilter, and staticFilter (order: kindFilter → accessibilityFilter → nameFilter → namespaceFilter → staticFilter → abstractFilter → maxResults; e.g. staticFilter true + abstractFilter true keeps static abstract interface members) so totalCount/truncated reflect the filtered set; no effect on non-type symbols or on Interfaces/Parameters. No match → empty Members, TotalCount = 0, Truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR; CLI: bare --abstract-filter = true, a value other than true / false (case-insensitive) is rejected."
            },
            sealedFilter = new
            {
                type = "boolean",
                description = "Optional sealedFilter (boolean) keeps only Members of a type symbol whose ISymbol.IsSealed is true when true (sealed override methods / properties / events; nested sealed classes and sealed records; nested structs, including readonly structs and record structs; nested enums and delegates, which Roslyn reports as sealed) or only Members whose IsSealed is false when false (ordinary, virtual, abstract and non-sealed override members; fields, const fields and enum members; nested unsealed, abstract and static classes and records; nested interfaces; every interface member, including an explicitly sealed default-bodied one, which Roslyn reports as non-virtual rather than IsSealed; every member of a struct, including an override such as ToString). A finalizer is returned in Members (as ~T()), matches false and is excluded by true. Judged on each member's own symbol: the target type itself is always returned whatever its own IsSealed, so a sealed class or struct target with no sealed members gives empty Members for true. Never-returned kinds: indexers (so a sealed override indexer is never returned), operators and conversion operators, instance and static constructors, property / event accessors and explicit interface implementations (including explicit interface indexers) never appear in Members, so sealedFilter = true cannot return them. Metadata targets work too: each metadata member is judged on its own IsSealed (e.g. on System.Collections.Generic.List<int> the nested Enumerator struct matches true while Add matches false). Peer differences: get_symbol_info sealedFilter filters only one target's Members, never removes the target, and works on metadata targets; search_symbols sealedFilter judges every name-matched symbol, is source-only, and drops namespaces when the filter is set; get_document_outline, get_type_hierarchy and find_implementations have abstractFilter but no sealedFilter. Versus abstractFilter: no member is both abstract and sealed, so abstractFilter true + sealedFilter true is always empty; a static class is neither sealed nor abstract. Versus staticFilter: static members and static classes are never sealed, so staticFilter true + sealedFilter true is always empty; nested structs, enums and delegates are sealed but not static, so they match staticFilter false + sealedFilter true. Combined with kindFilter, accessibilityFilter, nameFilter, namespaceFilter, staticFilter and abstractFilter (all must match) and applied before maxResults so totalCount / truncated reflect the filtered set; no effect on non-type symbols, including namespace targets (Members stays null), or on Interfaces or Parameters. No match gives empty Members, totalCount = 0, truncated = false. A non-boolean value (e.g. 'yes', the string 'true', or 1) is rejected with INTERNAL_ERROR. CLI: bare --sealed-filter = true, and a value other than true/false (case-insensitive) is rejected. Omitted = no sealed filter."
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
                KindFilter = args.KindFilter,
                AccessibilityFilter = args.AccessibilityFilter,
                NameFilter = args.NameFilter,
                NamespaceFilter = args.NamespaceFilter,
                StaticFilter = args.StaticFilter,
                AbstractFilter = args.AbstractFilter,
                SealedFilter = args.SealedFilter
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
        public string? AccessibilityFilter { get; init; }
        public string? NameFilter { get; init; }
        public string? NamespaceFilter { get; init; }
        public bool? StaticFilter { get; init; }
        public bool? AbstractFilter { get; init; }
        public bool? SealedFilter { get; init; }
    }
}

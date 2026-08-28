using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Query.Base;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Base;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Format;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Refactoring.Hierarchy;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Workspace;

namespace RoslynMcp.Cli;

/// <summary>
/// Represents a registered tool with its execution delegate and metadata.
/// </summary>
public sealed class ToolEntry
{
    /// <summary>Tool name in kebab-case.</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>The params DTO type (for help generation).</summary>
    public required Type ParamsType { get; init; }

    /// <summary>
    /// Execution delegate: (WorkspaceContext, JSON params string, CancellationToken) → result object.
    /// For the diagnose tool, WorkspaceContext may be null (uses IWorkspaceProvider directly).
    /// </summary>
    public required Func<WorkspaceContext?, string, CancellationToken, Task<object>> Execute { get; init; }

    /// <summary>Whether this tool requires a loaded workspace (most do, diagnose doesn't).</summary>
    public bool RequiresWorkspace { get; init; } = true;

    /// <summary>Tool category for help display.</summary>
    public required string Category { get; init; }
}

/// <summary>
/// Maps tool names to execution delegates for all 62 Roslyn tools.
/// </summary>
public sealed class ToolRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a refactoring operation that inherits from RefactoringOperationBase.
    /// </summary>
    public void RegisterRefactoring<TOp, TParams>(string name, string description)
        where TOp : RefactoringOperationBase<TParams>
        where TParams : class
    {
        _tools[name] = new ToolEntry
        {
            Name = name,
            Description = description,
            ParamsType = typeof(TParams),
            Category = "Refactoring",
            Execute = async (ctx, json, ct) =>
            {
                var op = (TOp)Activator.CreateInstance(typeof(TOp), ctx!)!;
                var p = JsonSerializer.Deserialize<TParams>(json, JsonOpts)!;
                return await op.ExecuteAsync(p, ct);
            }
        };
    }

    /// <summary>
    /// Register a query operation that inherits from QueryOperationBase.
    /// </summary>
    public void RegisterQuery<TOp, TParams, TResult>(string name, string description)
        where TOp : QueryOperationBase<TParams, TResult>
        where TParams : class
    {
        _tools[name] = new ToolEntry
        {
            Name = name,
            Description = description,
            ParamsType = typeof(TParams),
            Category = "Query",
            Execute = async (ctx, json, ct) =>
            {
                var op = (TOp)Activator.CreateInstance(typeof(TOp), ctx!)!;
                var p = JsonSerializer.Deserialize<TParams>(json, JsonOpts)!;
                return await op.ExecuteAsync(p, ct);
            }
        };
    }

    /// <summary>
    /// Register a tool with a manual execution delegate (for non-standard operations).
    /// </summary>
    public void RegisterManual(
        string name,
        string description,
        Type paramsType,
        string category,
        Func<WorkspaceContext?, string, CancellationToken, Task<object>> execute,
        bool requiresWorkspace = true)
    {
        _tools[name] = new ToolEntry
        {
            Name = name,
            Description = description,
            ParamsType = paramsType,
            Category = category,
            Execute = execute,
            RequiresWorkspace = requiresWorkspace
        };
    }

    /// <summary>
    /// Look up a tool by name (case-insensitive).
    /// </summary>
    public ToolEntry? GetTool(string name) =>
        _tools.TryGetValue(name, out var entry) ? entry : null;

    /// <summary>
    /// Get all registered tools sorted by category then name.
    /// </summary>
    public IReadOnlyList<ToolEntry> GetAllTools() =>
        _tools.Values.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();

    /// <summary>
    /// Build the default registry with all 62 tools registered.
    /// </summary>
    public static ToolRegistry BuildDefault()
    {
        var r = new ToolRegistry();

        // ── Refactoring: Extract (7) ──────────────────────────────────
        r.RegisterRefactoring<ExtractMethodOperation, ExtractMethodParams>(
            "extract-method", "Extract selected code into a new method");
        r.RegisterRefactoring<ExtractVariableOperation, ExtractVariableParams>(
            "extract-variable", "Extract an expression into a local variable");
        r.RegisterRefactoring<ExtractConstantOperation, ExtractConstantParams>(
            "extract-constant", "Extract a literal value into a named constant");
        r.RegisterRefactoring<ExtractInterfaceOperation, ExtractInterfaceParams>(
            "extract-interface", "Extract an interface from a class; separateFile writes {InterfaceName}.cs next to the source");
        r.RegisterRefactoring<ExtractBaseClassOperation, ExtractBaseClassParams>(
            "extract-base-class", "Extract a base class from common members; separateFile writes {BaseClassName}.cs next to the source");
        r.RegisterRefactoring<IntroduceParameterOperation, IntroduceParameterParams>(
            "introduce-parameter", "Introduce a method parameter from an expression");
        r.RegisterRefactoring<PullMembersUpOperation, PullMembersUpParams>(
            "pull-members-up", "Move selected members from a derived type onto an existing base class or interface");
        r.RegisterRefactoring<PushMembersDownOperation, PushMembersDownParams>(
            "push-members-down", "Move selected members from a base type down onto derived types");
        r.RegisterRefactoring<UseBaseTypeOperation, UseBaseTypeParams>(
            "use-base-type", "Replace derived-type references with a compatible base type or interface");
        r.RegisterRefactoring<IntroduceFieldOperation, IntroduceFieldParams>(
            "introduce-field", "Turn a selected local variable or expression into a class field");
        r.RegisterRefactoring<SafeDeleteOperation, SafeDeleteParams>(
            "safe-delete", "Delete a selected symbol only when it has no remaining references");
        r.RegisterRefactoring<MakeStaticOperation, MakeStaticParams>(
            "make-static", "Make a selected instance method static when it does not use instance state");
        r.RegisterRefactoring<MakeNonStaticOperation, MakeNonStaticParams>(
            "make-non-static", "Make a selected static method an instance method when a valid instance receiver exists");

        // ── Refactoring: Rename (3) ───────────────────────────────────
        r.RegisterRefactoring<RenameSymbolOperation, RenameSymbolParams>(
            "rename-symbol", "Rename any C# symbol with automatic reference updates");
        r.RegisterRefactoring<RenameFileToMatchTypeOperation, RenameFileToMatchTypeParams>(
            "rename-file-to-match-type", "Rename a file so its name matches the primary type declared in it");
        r.RegisterRefactoring<RenameNamespaceOperation, RenameNamespaceParams>(
            "rename-namespace", "Rename a namespace across the solution, updating declarations and references");

        // ── Refactoring: Inline (3) ───────────────────────────────────
        r.RegisterRefactoring<InlineVariableOperation, InlineVariableParams>(
            "inline-variable", "Inline a variable, replacing all references with its value");
        r.RegisterRefactoring<InlineMethodOperation, InlineMethodParams>(
            "inline-method", "Inline a method by replacing call sites with the method body");
        r.RegisterRefactoring<InlineConstantOperation, InlineConstantParams>(
            "inline-constant", "Inline a const field by replacing references with its literal value");

        // ── Refactoring: Signature (5) ────────────────────────────────
        r.RegisterRefactoring<ChangeSignatureOperation, ChangeSignatureParams>(
            "change-signature", "Add, remove, or reorder method parameters");
        r.RegisterRefactoring<AddParameterOperation, AddParameterParams>(
            "add-parameter", "Add a named parameter to a method and update call sites");
        r.RegisterRefactoring<RemoveParameterOperation, RemoveParameterParams>(
            "remove-parameter", "Remove a named parameter from a method and update call sites");
        r.RegisterRefactoring<ReorderParametersOperation, ReorderParametersParams>(
            "reorder-parameters", "Reorder a method's parameters by a 0-based permutation and update call sites");
        r.RegisterRefactoring<ChangeReturnTypeOperation, ChangeReturnTypeParams>(
            "change-return-type", "Change a method's return type and update return statements");

        // ── Refactoring: Encapsulate (1) ──────────────────────────────
        r.RegisterRefactoring<EncapsulateFieldOperation, EncapsulateFieldParams>(
            "encapsulate-field", "Encapsulate a field into a property");

        // ── Refactoring: Convert (9) ──────────────────────────────────
        r.RegisterRefactoring<ConvertToAsyncOperation, ConvertToAsyncParams>(
            "convert-to-async", "Convert a synchronous method to async");
        r.RegisterRefactoring<ConvertExpressionBodyOperation, ConvertExpressionBodyParams>(
            "convert-expression-body", "Toggle between expression body and block body");
        r.RegisterRefactoring<ConvertToBlockBodyOperation, ConvertToBlockBodyParams>(
            "convert-to-block-body", "Convert an expression-bodied member to a block body");
        r.RegisterRefactoring<ConvertPropertyOperation, ConvertPropertyParams>(
            "convert-property", "Convert between auto-property and full property");
        r.RegisterRefactoring<ConvertForeachLinqOperation, ConvertForeachLinqParams>(
            "convert-foreach-linq", "Convert between foreach loop and LINQ expression");
        r.RegisterRefactoring<ConvertAnonymousToClassOperation, ConvertAnonymousToClassParams>(
            "convert-anonymous-to-class", "Convert an anonymous type to a named class or record");
        r.RegisterRefactoring<ConvertTupleToStructOperation, ConvertTupleToStructParams>(
            "convert-tuple-to-struct", "Convert a tuple to a named struct");
        r.RegisterRefactoring<ConvertToInterpolatedStringOperation, ConvertToInterpolatedStringParams>(
            "convert-to-interpolated-string", "Convert string concatenation to interpolated string");
        r.RegisterRefactoring<ConvertToPatternMatchingOperation, ConvertToPatternMatchingParams>(
            "convert-to-pattern-matching", "Convert type checks to pattern matching");

        // ── Refactoring: Generate (9) ─────────────────────────────────
        r.RegisterRefactoring<GenerateConstructorOperation, GenerateConstructorParams>(
            "generate-constructor", "Generate a constructor from fields/properties; includeProperties (default true) includes settable properties; false uses instance fields only unless members names a property; includeInheritedMembers (default false) also collects accessible instance fields/settable properties declared on base types; replaceExisting (default false) replaces an existing constructor with the exact same signature instead of failing; visibility (default public) sets constructor accessibility");
        r.RegisterRefactoring<GeneratePropertyOperation, GeneratePropertyParams>(
            "generate-property", "Generate a property on a type (auto, init-only, or backing-field)");
        r.RegisterRefactoring<GenerateMethodStubOperation, GenerateMethodStubParams>(
            "generate-method-stub", "Generate a method from an undefined call site, inferring the signature from usage");
        r.RegisterRefactoring<GenerateEqualsHashCodeOperation, GenerateEqualsHashCodeParams>(
            "generate-equals-hashcode", "Generate Equals and GetHashCode overrides; implementIEquatable (default false) also adds IEquatable<T>; generateOperators (default false) also adds == and !=; replaceExisting (default false) replaces existing equality members instead of failing; useHashCodeCombine (default true) uses HashCode.Combine / builder instead of an unchecked prime-multiply GetHashCode; includeProperties (default true) includes readable properties as equality members; false uses instance fields only unless fields names a property; callSuper (default false) folds the immediate base type's Equals/GetHashCode into the generated methods; includeInheritedMembers (default false) also collects accessible instance fields/properties declared on base types");
        r.RegisterRefactoring<GenerateOverridesOperation, GenerateOverridesParams>(
            "generate-overrides", "Generate method overrides from a base class");
        r.RegisterRefactoring<GenerateToStringOperation, GenerateToStringParams>(
            "generate-tostring", "Generate a ToString override; format interpolated (default) or stringbuilder; includeProperties (default true) includes readable properties as ToString members; false uses instance fields only unless fields names a property; includeInheritedMembers (default false) also collects accessible instance fields/properties declared on base types; replaceExisting (default false) replaces an existing parameterless ToString instead of failing");
        r.RegisterRefactoring<ImplementInterfaceOperation, ImplementInterfaceParams>(
            "implement-interface", "Generate implementation stubs for an interface");
        r.RegisterRefactoring<ImplementAbstractOperation, ImplementAbstractParams>(
            "implement-abstract", "Generate implementation stubs for unimplemented abstract members inherited by a class");
        r.RegisterRefactoring<AddNullChecksOperation, AddNullChecksParams>(
            "add-null-checks", "Add null checks to method parameters");

        // ── Refactoring: Organize (3) ─────────────────────────────────
        r.RegisterRefactoring<AddMissingUsingsOperation, AddMissingUsingsParams>(
            "add-missing-usings", "Add missing using directives");
        r.RegisterRefactoring<RemoveUnusedUsingsOperation, RemoveUnusedUsingsParams>(
            "remove-unused-usings", "Remove unused using directives");
        r.RegisterRefactoring<SortUsingsOperation, SortUsingsParams>(
            "sort-usings", "Sort using directives; systemFirst (default true) places System / System.* first");

        // ── Refactoring: Format (1) ───────────────────────────────────
        r.RegisterRefactoring<FormatDocumentOperation, FormatDocumentParams>(
            "format-document", "Format a C# document according to conventions");

        // ── Refactoring: Move (2) — non-standard base class ──────────
        r.RegisterManual("move-type-to-file",
            "Move a type declaration to its own file",
            typeof(MoveTypeToFileParams), "Refactoring",
            async (ctx, json, ct) =>
            {
                var op = new MoveTypeToFileOperation(ctx!);
                var p = JsonSerializer.Deserialize<MoveTypeToFileParams>(json, JsonOpts)!;
                return await op.ExecuteAsync(p, ct);
            });

        r.RegisterManual("move-type-to-namespace",
            "Move a type to a different namespace",
            typeof(MoveTypeToNamespaceParams), "Refactoring",
            async (ctx, json, ct) =>
            {
                var op = new MoveTypeToNamespaceOperation(ctx!);
                var p = JsonSerializer.Deserialize<MoveTypeToNamespaceParams>(json, JsonOpts)!;
                return await op.ExecuteAsync(p, ct);
            });

        // ── Query: Navigation (5) ────────────────────────────────────
        r.RegisterQuery<FindReferencesOperation, FindReferencesParams, FindReferencesResult>(
            "find-references", "Find all references to a symbol across the solution");
        r.RegisterQuery<FindCallersOperation, FindCallersParams, FindCallersResult>(
            "find-callers", "Find all callers of a method");
        r.RegisterQuery<FindImplementationsOperation, FindImplementationsParams, FindImplementationsResult>(
            "find-implementations", "Find all implementations of an interface or abstract member");
        r.RegisterQuery<GoToDefinitionOperation, GoToDefinitionParams, GoToDefinitionResult>(
            "go-to-definition", "Navigate to the definition of a symbol");
        r.RegisterQuery<SearchSymbolsOperation, SearchSymbolsParams, SearchSymbolsResult>(
            "search-symbols", "Search for symbols by name pattern");

        // ── Query: Analysis (6) ──────────────────────────────────────
        r.RegisterQuery<GetDiagnosticsOperation, GetDiagnosticsParams, GetDiagnosticsResult>(
            "get-diagnostics", "Get compiler diagnostics for the solution or a file");
        r.RegisterQuery<GetCodeMetricsOperation, GetCodeMetricsParams, GetCodeMetricsResult>(
            "get-code-metrics", "Calculate code metrics (complexity, coupling, etc.)");
        r.RegisterQuery<AnalyzeControlFlowOperation, AnalyzeControlFlowParams, AnalyzeControlFlowResult>(
            "analyze-control-flow", "Analyze control flow paths in a method");
        r.RegisterQuery<AnalyzeDataFlowOperation, AnalyzeDataFlowParams, AnalyzeDataFlowResult>(
            "analyze-data-flow", "Analyze data flow (reads, writes, captures) in a region");
        r.RegisterQuery<GetDocumentOutlineOperation, GetDocumentOutlineParams, GetDocumentOutlineResult>(
            "get-document-outline", "Get a structural outline of a C# document");
        r.RegisterQuery<GetSymbolInfoOperation, GetSymbolInfoParams, DetailedSymbolInfo>(
            "get-symbol-info", "Get detailed information about a symbol at a position");

        // ── Query: Type Hierarchy (1) ────────────────────────────────
        r.RegisterQuery<GetTypeHierarchyOperation, GetTypeHierarchyParams, GetTypeHierarchyResult>(
            "get-type-hierarchy", "Get the inheritance hierarchy for a type");

        // ── Diagnose (1) — special, no workspace required ────────────
        r.RegisterManual("diagnose",
            "Check the health of the Roslyn environment and workspace status",
            typeof(DiagnoseParams), "Diagnostic",
            (_, json, ct) =>
            {
                // Diagnose is handled specially in Program.cs; this registration
                // exists for help generation and tool listing purposes.
                // The actual execution is in Program.cs since it needs IWorkspaceProvider,
                // not WorkspaceContext.
                throw new InvalidOperationException(
                    "Diagnose is handled directly in Program.cs, not via generic dispatch.");
            },
            requiresWorkspace: false);

        return r;
    }
}

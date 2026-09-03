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
/// Maps tool names to execution delegates for all 66 Roslyn tools.
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
    /// Build the default registry with all 66 tools registered.
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
            "extract-interface", "Extract an interface from a class; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; separateFile writes {InterfaceName}.cs next to the source");
        r.RegisterRefactoring<ExtractBaseClassOperation, ExtractBaseClassParams>(
            "extract-base-class", "Extract a base class from common members; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; separateFile writes {BaseClassName}.cs next to the source");
        r.RegisterRefactoring<IntroduceParameterOperation, IntroduceParameterParams>(
            "introduce-parameter", "Introduce a method parameter from a local variable; column (optional) picks the matching local whose identifier or declaration span covers that column when set with line; omitted keeps today's start-line equality on the local declaration statement, then variableName FirstOrDefault; a continuation-line identifier is eligible when column is set");
        r.RegisterRefactoring<PullMembersUpOperation, PullMembersUpParams>(
            "pull-members-up", "Move selected members from a derived type onto an existing base class or interface; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter");
        r.RegisterRefactoring<PushMembersDownOperation, PushMembersDownParams>(
            "push-members-down", "Move selected members from a base type down onto derived types; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter");
        r.RegisterRefactoring<UseBaseTypeOperation, UseBaseTypeParams>(
            "use-base-type", "Replace derived-type references with a compatible base type or interface; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick (simple name / single match) or FQN semantic narrowing; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; sourceFile and typeName are required when allFiles is omitted or false; allFiles (default false) rewrites eligible references of every type declaration in every C# file (sourceFile optional when true; cannot be combined with typeName, line, or column)");
        r.RegisterRefactoring<IntroduceFieldOperation, IntroduceFieldParams>(
            "introduce-field", "Turn a selected local variable or expression into a class field");
        r.RegisterRefactoring<SafeDeleteOperation, SafeDeleteParams>(
            "safe-delete", "Delete a selected symbol only when it has no remaining references");
        r.RegisterRefactoring<MakeStaticOperation, MakeStaticParams>(
            "make-static", "Make a selected instance method static when it does not use instance state; sourceFile and selection (startLine / startColumn / endLine / endColumn) are required when allFiles is omitted or false; allFiles (default false) makes every eligible ordinary instance method static in every C# file (sourceFile optional when true; cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName)");
        r.RegisterRefactoring<MakeNonStaticOperation, MakeNonStaticParams>(
            "make-non-static", "Make a selected static method an instance method when a valid instance receiver exists; sourceFile and selection (startLine / startColumn / endLine / endColumn) are required when allFiles is omitted or false; allFiles (default false) makes every eligible ordinary static method an instance method in every C# file (sourceFile optional when true; cannot be combined with startLine, startColumn, endLine, endColumn, or symbolName)");

        // ── Refactoring: Rename (3) ───────────────────────────────────
        r.RegisterRefactoring<RenameSymbolOperation, RenameSymbolParams>(
            "rename-symbol", "Rename any C# symbol with automatic reference updates");
        r.RegisterRefactoring<RenameFileToMatchTypeOperation, RenameFileToMatchTypeParams>(
            "rename-file-to-match-type", "Rename a file so its name matches the primary type declared in it; column (optional) picks the smallest type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's omitted-line path; allFiles (default false) renames every unambiguous mismatched single-type C# file (sourceFile optional when true; cannot be combined with typeName, line, or column)");
        r.RegisterRefactoring<RenameNamespaceOperation, RenameNamespaceParams>(
            "rename-namespace", "Rename a namespace across the solution, updating declarations and references; column (optional) picks the smallest namespace whose name or declaration span covers that column when set with line; omitted keeps today's namespaceName + optional line pick; column without line keeps today's omitted-line path");

        // ── Refactoring: Inline (3) ───────────────────────────────────
        r.RegisterRefactoring<InlineVariableOperation, InlineVariableParams>(
            "inline-variable", "Inline a variable, replacing all references with its value; column (optional) picks the declaration whose identifier or declaration span covers that column; omitted keeps today's variableName + optional line start-line pick");
        r.RegisterRefactoring<InlineMethodOperation, InlineMethodParams>(
            "inline-method", "Inline a method by replacing call sites with the method body; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line identifier start-line pick");
        r.RegisterRefactoring<InlineConstantOperation, InlineConstantParams>(
            "inline-constant", "Inline a const field by replacing references with its literal value; line (optional) picks the matching const field whose identifier or declaration span covers that line when several constants share the name; omitted keeps today's constantName + optional typeName path including SymbolAmbiguous; column (optional) picks the matching const field whose identifier or declaration span covers that column when set with line; omitted keeps today's constantName + optional typeName + optional line pick; column without line keeps today's omitted-line path after the name/typeName filter; sourceFile and constantName are required when allFiles is omitted or false; allFiles (default false) inlines every eligible const field in every C# file (sourceFile optional when true; cannot be combined with constantName, typeName, line, or column); removeConstant and preview remain valid with allFiles");

        // ── Refactoring: Signature (5) ────────────────────────────────
        r.RegisterRefactoring<ChangeSignatureOperation, ChangeSignatureParams>(
            "change-signature", "Add, remove, or reorder method parameters; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick");
        r.RegisterRefactoring<AddParameterOperation, AddParameterParams>(
            "add-parameter", "Add a named parameter to a method and update call sites; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick");
        r.RegisterRefactoring<RemoveParameterOperation, RemoveParameterParams>(
            "remove-parameter", "Remove a named parameter from a method and update call sites; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick");
        r.RegisterRefactoring<ReorderParametersOperation, ReorderParametersParams>(
            "reorder-parameters", "Reorder a method's parameters by a 0-based permutation and update call sites; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick");
        r.RegisterRefactoring<ChangeReturnTypeOperation, ChangeReturnTypeParams>(
            "change-return-type", "Change a method's return type and update return statements; column (optional) picks the smallest method whose identifier or declaration span covers that column; omitted keeps today's methodName and/or line start-line pick");

        // ── Refactoring: Encapsulate (1) ──────────────────────────────
        r.RegisterRefactoring<EncapsulateFieldOperation, EncapsulateFieldParams>(
            "encapsulate-field", "Encapsulate a field into a property; line (optional) picks the field whose identifier or declaration span covers that line when several fields share the name; omitted keeps today's fieldName FirstOrDefault pick; column (optional) picks the field whose identifier or declaration span covers that column when set with line; omitted keeps today's fieldName + optional line pick; column without line keeps today's first-match after the fieldName filter; updateReferences (default true) rewrites external references to the new property, false leaves external callers on the field; allFiles (default false) encapsulates every eligible field in every C# file (sourceFile optional when true; cannot be combined with fieldName, line, column, or propertyName)");

        // ── Refactoring: Convert (13) ─────────────────────────────────
        r.RegisterRefactoring<ConvertToAsyncOperation, ConvertToAsyncParams>(
            "convert-to-async", "Convert a synchronous method to async; column (optional) picks the method whose identifier or declaration span covers that column; updateCallers (default false) awaits already-async callers and skips sync callers that cannot legally await; allFiles (default false) converts every distinct eligible sync method in every C# file (sourceFile optional when true; cannot be combined with methodName, line, or column)");
        r.RegisterRefactoring<ConvertExpressionBodyOperation, ConvertExpressionBodyParams>(
            "convert-expression-body", "Toggle between expression body and block body; column (optional) picks the member whose identifier or declaration span covers that column on the given line; allFiles (default false) converts every eligible supported member in every C# file (sourceFile optional when true; cannot be combined with memberName, line, or column)");
        r.RegisterRefactoring<ConvertToBlockBodyOperation, ConvertToBlockBodyParams>(
            "convert-to-block-body", "Convert an expression-bodied member to a block body; column (optional) picks the member whose identifier or declaration span covers that column on the given line");
        r.RegisterRefactoring<InvertIfOperation, InvertIfParams>(
            "invert-if", "Flip an if-statement condition and swap the if/else branches; column (optional) picks the if whose IfKeyword span covers that column on the given line (exclusive-end; FirstOrDefault among covering keywords); omitted keeps today's first-IfKeyword-on-line-by-SpanStart pick; column without line keeps today's required-line validation; allFiles (default false) inverts every distinct eligible if in every C# file (sourceFile optional when true; cannot be combined with line or column)");
        r.RegisterRefactoring<AddBracesOperation, AddBracesParams>(
            "add-braces", "Add braces to control statements (if, else, for, foreach, while, using) that have a single-statement body; scope is statement (default; single-file only), file, or type (single-file only); column (optional) picks the control statement whose keyword span covers that column on the given line (exclusive-end; shortest keyword / First among covering); omitted keeps today's first/leftmost-keyword-on-line-by-SpanStart pick; column without line keeps today's required-line validation; allFiles (default false) wraps every C# file at file scope (sourceFile optional when true; omitted scope uses file; cannot combine with scope=statement or scope=type)");
        r.RegisterRefactoring<RemoveBracesOperation, RemoveBracesParams>(
            "remove-braces", "Remove braces from control statements (if, else, for, foreach, while, using) that have a single-statement braced body; scope is statement (default; single-file only), file, or type (single-file only); column (optional) picks the control statement whose keyword span covers that column on the given line (exclusive-end; shortest keyword / First among covering); omitted keeps today's first/leftmost-keyword-on-line-by-SpanStart pick; column without line keeps today's required-line validation; allFiles (default false) unwraps every C# file at file scope (sourceFile optional when true; omitted scope uses file; cannot combine with scope=statement or scope=type)");
        r.RegisterRefactoring<SimplifyNameOperation, SimplifyNameParams>(
            "simplify-name", "Remove redundant namespace qualifications from type references; scope is file (default) or location (requires line); column (optional) picks the name whose span covers that column on the given line (exclusive-end; FirstOrDefault among covering names); omitted keeps today's first/leftmost-name-on-line-by-SpanStart pick; column without line keeps today's required-line validation; allFiles (default false) simplifies every C# file (sourceFile optional when true)");
        r.RegisterRefactoring<ConvertPropertyOperation, ConvertPropertyParams>(
            "convert-property", "Convert between auto-property and full property; column (optional) picks the smallest property whose identifier or declaration span covers that column on the given line; allFiles (default false) converts every distinct eligible property in every C# file (sourceFile optional when true; cannot be combined with propertyName, line, or column)");
        r.RegisterRefactoring<ConvertForeachLinqOperation, ConvertForeachLinqParams>(
            "convert-foreach-linq", "Convert foreach loops with Add patterns to LINQ; preferQuerySyntax (default false) keeps method syntax, true emits query syntax (from … where … select) for filter/project/ToList; Any/All/FirstOrDefault/Count keep method syntax; column disambiguates two foreach statements on one line; allFiles (default false) converts every distinct eligible foreach in every C# file (sourceFile optional when true; cannot be combined with line or column)");
        r.RegisterRefactoring<ConvertAnonymousToClassOperation, ConvertAnonymousToClassParams>(
            "convert-anonymous-to-class", "Convert an anonymous type to a named class or record");
        r.RegisterRefactoring<ConvertTupleToStructOperation, ConvertTupleToStructParams>(
            "convert-tuple-to-struct", "Convert a tuple to a named struct");
        r.RegisterRefactoring<ConvertToInterpolatedStringOperation, ConvertToInterpolatedStringParams>(
            "convert-to-interpolated-string", "Convert string.Format() and concatenation to interpolated strings; column (optional) picks the Format invocation or concatenation whose span covers that column on the given line; allFiles (default false) converts every distinct convertible Format invocation and outer concatenation in every C# file (sourceFile optional when true; cannot be combined with line or column)");
        r.RegisterRefactoring<ConvertToPatternMatchingOperation, ConvertToPatternMatchingParams>(
            "convert-to-pattern-matching", "Convert type checks to pattern matching; column (optional) picks the smallest switch or if whose span covers that column on the given line; allFiles (default false) converts every distinct eligible switch/if-chain in every C# file (sourceFile optional when true; cannot be combined with line or column)");

        // ── Refactoring: Generate (9) ─────────────────────────────────
        r.RegisterRefactoring<GenerateConstructorOperation, GenerateConstructorParams>(
            "generate-constructor", "Generate a constructor from fields/properties; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; includeProperties (default true) includes settable properties; false uses instance fields only unless members names a property; includeInheritedMembers (default false) also collects accessible instance fields/settable properties declared on base types; replaceExisting (default false) replaces an existing constructor with the exact same signature instead of failing; visibility (default public) sets constructor accessibility; copyConstructor (default false) generates a single-parameter same-type copy constructor instead of one parameter per member; classBaseCopy (default false) emits : base((Base)<copyParameter>) on an ordinary class when the immediate base has an accessible copy constructor — requires copyConstructor; callBase (default false) emits : base(...) on an ordinary class or record class when an accessible immediate-base constructor's parameter types are a prefix of the generated constructor — conflicts with copyConstructor; allFiles (default false) generates constructors for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, members, line, or column)");
        r.RegisterRefactoring<GeneratePropertyOperation, GeneratePropertyParams>(
            "generate-property", "Generate a property on a type (auto, init-only, or backing-field); line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; replaceExisting (default false) replaces an existing property of the same name instead of failing");
        r.RegisterRefactoring<GenerateMethodStubOperation, GenerateMethodStubParams>(
            "generate-method-stub", "Generate a method from an undefined call site, inferring the signature from usage; throwNotImplemented (default true) throws NotImplementedException in the stub body, false uses default-return / empty void bodies; replaceExisting (default false) replaces a compatible ordinary method instead of failing");
        r.RegisterRefactoring<GenerateEqualsHashCodeOperation, GenerateEqualsHashCodeParams>(
            "generate-equals-hashcode", "Generate Equals and GetHashCode overrides; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; implementIEquatable (default false) also adds IEquatable<T>; generateOperators (default false) also adds == and !=; replaceExisting (default false) replaces existing equality members instead of failing; useHashCodeCombine (default true) uses HashCode.Combine / builder instead of an unchecked prime-multiply GetHashCode; includeProperties (default true) includes readable properties as equality members; false uses instance fields only unless fields names a property; callSuper (default false) folds the immediate base type's Equals/GetHashCode into the generated methods; includeInheritedMembers (default false) also collects accessible instance fields/properties declared on base types; allFiles (default false) generates Equals/GetHashCode for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, fields, line, or column)");
        r.RegisterRefactoring<GenerateOverridesOperation, GenerateOverridesParams>(
            "generate-overrides", "Generate method, property/indexer, and event overrides from a base class; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; callBase (default true) emits base.Method() / base.Prop / base[i] for non-abstract virtuals (events always use empty add/remove); replaceExisting (default false) replaces already-overridden members instead of skipping them; allFiles (default false) generates missing overrides for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, members, line, or column)");
        r.RegisterRefactoring<GenerateToStringOperation, GenerateToStringParams>(
            "generate-tostring", "Generate a ToString override; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; format interpolated (default) or stringbuilder; includeProperties (default true) includes readable properties as ToString members; false uses instance fields only unless fields names a property; includeInheritedMembers (default false) also collects accessible instance fields/properties declared on base types; replaceExisting (default false) replaces an existing parameterless ToString instead of failing; callSuper (default false) folds the immediate base type's ToString into the generated override; allFiles (default false) generates ToString for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, fields, line, or column)");
        r.RegisterRefactoring<ImplementInterfaceOperation, ImplementInterfaceParams>(
            "implement-interface", "Generate implementation stubs for an interface; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; throwNotImplemented (default true) throws NotImplementedException in stub bodies; replaceExisting (default false) replaces already-implemented interface members instead of failing; preview returns computed changes without applying; allFiles (default false) implements missing members of already-declared interfaces for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, interfaceName, members, line, or column)");
        r.RegisterRefactoring<ImplementAbstractOperation, ImplementAbstractParams>(
            "implement-abstract", "Generate implementation stubs for unimplemented abstract members inherited by a class; line (optional) picks the type whose identifier or declaration span covers that line when several types share the name; omitted keeps today's typeName FirstOrDefault pick; column (optional) picks the type whose identifier or declaration span covers that column when set with line; omitted keeps today's typeName + optional line pick; column without line keeps today's first-match after the typeName filter; throwNotImplemented (default true) throws NotImplementedException in stub bodies, false uses default-return / empty setter bodies; replaceExisting (default false) replaces already-implemented abstract members instead of failing; preview returns computed changes without applying; allFiles (default false) implements missing abstract members for every eligible type in every C# file (sourceFile optional when true; cannot be combined with typeName, members, line, or column)");
        r.RegisterRefactoring<AddNullChecksOperation, AddNullChecksParams>(
            "add-null-checks", "Add null checks to method or constructor parameters; column (optional) picks the smallest method or constructor whose identifier or declaration span covers that column; omitted keeps today's methodName and optional line start-line pick; allFiles (default false) adds checks to every eligible method or constructor with a block body in every C# file (sourceFile optional when true; cannot be combined with methodName, line, or column)");

        // ── Refactoring: Organize (3) ─────────────────────────────────
        r.RegisterRefactoring<AddMissingUsingsOperation, AddMissingUsingsParams>(
            "add-missing-usings", "Add missing using directives");
        r.RegisterRefactoring<RemoveUnusedUsingsOperation, RemoveUnusedUsingsParams>(
            "remove-unused-usings", "Remove unused using directives");
        r.RegisterRefactoring<SortUsingsOperation, SortUsingsParams>(
            "sort-usings", "Sort using directives; systemFirst (default true) places System / System.* first; allFiles (default false) sorts every C# file (sourceFile optional when true)");

        // ── Refactoring: Format (1) ───────────────────────────────────
        r.RegisterRefactoring<FormatDocumentOperation, FormatDocumentParams>(
            "format-document", "Format a C# document according to conventions; preview returns computed changes without applying; allFiles (default false) formats every C# file (sourceFile optional when true)");

        // ── Refactoring: Move (2) — non-standard base class ──────────
        r.RegisterManual("move-type-to-file",
            "Move a type declaration to its own file; line (optional) disambiguates same-named top-level types by start-line equality; omitted with several matches is SymbolAmbiguous; a single match ignores line; column (optional) picks the top-level type whose identifier or declaration span covers that column when set with line; omitted keeps today's symbolName + optional line pick; column without line keeps today's omitted-line path; sourceFile, symbolName, and targetFile are required when allFiles is omitted or false; allFiles (default false) extracts every eligible top-level type into {directory}/{TypeName}.cs in every C# file (sourceFile optional when true — limits the walk to that file; cannot be combined with symbolName, targetFile, line, or column)",
            typeof(MoveTypeToFileParams), "Refactoring",
            async (ctx, json, ct) =>
            {
                var op = new MoveTypeToFileOperation(ctx!);
                var p = JsonSerializer.Deserialize<MoveTypeToFileParams>(json, JsonOpts)!;
                return await op.ExecuteAsync(p, ct);
            });

        r.RegisterManual("move-type-to-namespace",
            "Move a type to a different namespace; line (optional) disambiguates same-named top-level types by start-line equality; omitted with several matches is SymbolAmbiguous; a single match ignores line; column (optional) picks the top-level type whose identifier or declaration span covers that column when set with line; omitted keeps today's symbolName + optional line pick; column without line keeps today's omitted-line path; sourceFile and symbolName are required when allFiles is omitted or false; allFiles (default false) moves every eligible top-level type into targetNamespace in every C# file (sourceFile optional when true — limits the walk to that file; cannot be combined with symbolName, line, or column); updateFileLocation and preview remain valid with allFiles",
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
            "get-code-metrics", "Calculate code metrics (complexity, coupling, etc.); column (optional) disambiguates same-named symbols the same way find_callers does when set with line; omitted keeps today's Line-only / file-level path; column without line keeps today's SymbolResolver omitted-line path");
        r.RegisterQuery<AnalyzeControlFlowOperation, AnalyzeControlFlowParams, AnalyzeControlFlowResult>(
            "analyze-control-flow", "Analyze control flow paths in a method; startColumn / endColumn (optional) trim the region (1-based; Roslyn Character = column - 1); omitted keeps today's whole-line span (start of startLine through end of endLine)");
        r.RegisterQuery<AnalyzeDataFlowOperation, AnalyzeDataFlowParams, AnalyzeDataFlowResult>(
            "analyze-data-flow", "Analyze data flow (reads, writes, captures) in a region; startColumn / endColumn (optional) trim the region (1-based; Roslyn Character = column - 1); omitted keeps today's whole-line span (start of startLine through end of endLine)");
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

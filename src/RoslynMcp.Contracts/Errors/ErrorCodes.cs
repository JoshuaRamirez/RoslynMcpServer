namespace RoslynMcp.Contracts.Errors;

/// <summary>
/// Machine-readable error codes for refactoring operations.
/// Organized by category: 1xxx=Input, 2xxx=Resource, 3xxx=Semantic, 4xxx=System, 5xxx=Environment.
/// </summary>
public static class ErrorCodes
{
    // ============================================
    // Input Validation Errors (1xxx)
    // ============================================

    /// <summary>Source file path is malformed.</summary>
    public const string InvalidSourcePath = "1001";

    /// <summary>Target file path is malformed.</summary>
    public const string InvalidTargetPath = "1002";

    /// <summary>Symbol name contains invalid characters.</summary>
    public const string InvalidSymbolName = "1003";

    /// <summary>Namespace format is invalid.</summary>
    public const string InvalidNamespace = "1004";

    /// <summary>Required parameter not provided.</summary>
    public const string MissingRequiredParam = "1005";

    /// <summary>Line number out of valid range.</summary>
    public const string InvalidLineNumber = "1006";

    /// <summary>Column number out of valid range.</summary>
    public const string InvalidColumnNumber = "1007";

    /// <summary>Selection start must be before end.</summary>
    public const string InvalidSelectionRange = "1008";

    /// <summary>New name is invalid for this symbol type.</summary>
    public const string InvalidNewName = "1009";

    /// <summary>Parameter type is not valid C# type.</summary>
    public const string InvalidParameterType = "1010";

    /// <summary>Parameter position out of range.</summary>
    public const string InvalidParameterPosition = "1011";

    /// <summary>Member list contains invalid member names.</summary>
    public const string InvalidMemberList = "1012";

    /// <summary>Default value does not match parameter type.</summary>
    public const string InvalidDefaultValue = "1013";

    /// <summary>Visibility modifier is not valid.</summary>
    public const string InvalidVisibility = "1014";

    /// <summary>Return type is not valid C# type.</summary>
    public const string InvalidReturnType = "1015";

    /// <summary>Selection range is empty.</summary>
    public const string EmptySelection = "1016";

    // ============================================
    // Resource Errors (2xxx)
    // ============================================

    /// <summary>Source file does not exist.</summary>
    public const string SourceFileNotFound = "2001";

    /// <summary>Source file not part of loaded solution.</summary>
    public const string SourceNotInWorkspace = "2002";

    /// <summary>No symbol found at specified location.</summary>
    public const string SymbolNotFound = "2003";

    /// <summary>Multiple symbols match; provide line number.</summary>
    public const string SymbolAmbiguous = "2004";

    /// <summary>No solution currently loaded.</summary>
    public const string WorkspaceNotLoaded = "2005";

    /// <summary>No method found at specified location.</summary>
    public const string MethodNotFound = "2006";

    /// <summary>No variable found at specified location.</summary>
    public const string VariableNotFound = "2007";

    /// <summary>No field found at specified location.</summary>
    public const string FieldNotFound = "2008";

    /// <summary>Interface not found in workspace.</summary>
    public const string InterfaceNotFound = "2009";

    /// <summary>Base class not found.</summary>
    public const string BaseClassNotFound = "2010";

    /// <summary>No derived classes found.</summary>
    public const string DerivedClassesNotFound = "2011";

    /// <summary>Member not found in type.</summary>
    public const string MemberNotFound = "2012";

    /// <summary>No expression found at selection.</summary>
    public const string ExpressionNotFound = "2013";

    /// <summary>No statement found at selection.</summary>
    public const string StatementNotFound = "2014";

    /// <summary>Type not found at specified location.</summary>
    public const string TypeNotFound = "2015";

    /// <summary>Parameter not found.</summary>
    public const string ParameterNotFound = "2016";

    /// <summary>No constructor found.</summary>
    public const string ConstructorNotFound = "2017";

    /// <summary>No overridable member found.</summary>
    public const string OverrideTargetNotFound = "2018";

    /// <summary>No implementations found for symbol.</summary>
    public const string NoImplementationsFound = "2019";

    /// <summary>Symbol kind not valid for requested query.</summary>
    public const string InvalidSymbolKind = "2020";

    // ============================================
    // Semantic Errors (3xxx)
    // ============================================

    /// <summary>Symbol type cannot be moved (method, field, etc.).</summary>
    public const string SymbolNotMoveable = "3001";

    /// <summary>Nested types cannot be moved independently.</summary>
    public const string SymbolIsNested = "3002";

    /// <summary>Target location already has type with same name.</summary>
    public const string NameCollision = "3003";

    /// <summary>Source and target are the same.</summary>
    public const string SameLocation = "3004";

    /// <summary>Move would create circular dependency.</summary>
    public const string CircularReference = "3005";

    /// <summary>Move would break accessibility constraints.</summary>
    public const string BreaksAccessibility = "3006";

    // --------------------------------------------
    // Rename Errors (3010-3018)
    // --------------------------------------------

    /// <summary>New name conflicts with existing symbol in scope.</summary>
    public const string NameConflictScope = "3010";

    /// <summary>New name is a C# reserved keyword.</summary>
    public const string ReservedKeyword = "3011";

    /// <summary>New name hides inherited member.</summary>
    public const string HidesBaseMember = "3012";

    /// <summary>New name conflicts with type parameter.</summary>
    public const string ConflictsWithTypeParameter = "3013";

    /// <summary>Rename would create ambiguous overloads.</summary>
    public const string RenameWouldBreakOverload = "3014";

    /// <summary>Constructors cannot be renamed directly.</summary>
    public const string CannotRenameConstructor = "3015";

    /// <summary>Destructors cannot be renamed directly.</summary>
    public const string CannotRenameDestructor = "3016";

    /// <summary>Operators cannot be renamed.</summary>
    public const string CannotRenameOperator = "3017";

    /// <summary>Cannot rename symbols from external assemblies.</summary>
    public const string CannotRenameExternal = "3018";

    // --------------------------------------------
    // Extract Errors (3030-3039)
    // --------------------------------------------

    /// <summary>Selection does not form valid extractable code.</summary>
    public const string InvalidSelection = "3030";

    /// <summary>Selection contains yield statements.</summary>
    public const string ContainsYield = "3031";

    /// <summary>Selection has unresolvable control flow.</summary>
    public const string UnresolvableControlFlow = "3032";

    /// <summary>Selection crosses incompatible scopes.</summary>
    public const string SelectionCrossesScopes = "3033";

    /// <summary>Selection has multiple entry points.</summary>
    public const string MultipleEntryPoints = "3034";

    /// <summary>Selection has multiple exit points.</summary>
    public const string MultipleExitPoints = "3035";

    /// <summary>Type has no extractable members.</summary>
    public const string NoExtractableMembers = "3036";

    /// <summary>Expression is not a compile-time constant.</summary>
    public const string NotCompileTimeConstant = "3037";

    /// <summary>Expression has side effects.</summary>
    public const string ExpressionHasSideEffects = "3038";

    /// <summary>Selection captures ref local.</summary>
    public const string CapturesRefLocal = "3039";

    // --------------------------------------------
    // Generate Errors (3060-3065)
    // --------------------------------------------

    /// <summary>Constructor with same signature exists.</summary>
    public const string ConstructorExists = "3060";

    /// <summary>Interface member already implemented.</summary>
    public const string MemberAlreadyImplemented = "3061";

    /// <summary>Override already exists.</summary>
    public const string OverrideAlreadyExists = "3062";

    /// <summary>No overridable members.</summary>
    public const string NoOverridableMembers = "3063";

    /// <summary>Cannot add constructor to static class.</summary>
    public const string TypeIsStatic = "3064";

    /// <summary>Interface member conflicts.</summary>
    public const string InterfaceMemberConflict = "3065";

    // --------------------------------------------
    // Extract Interface/Base Class Errors (3070-3079)
    // --------------------------------------------

    /// <summary>Interface with same name already exists.</summary>
    public const string InterfaceAlreadyExists = "3070";

    /// <summary>Cannot extract from static type.</summary>
    public const string CannotExtractFromStatic = "3071";

    /// <summary>Member is not public and cannot be extracted to interface.</summary>
    public const string MemberNotPublic = "3072";

    /// <summary>Type already has a base class other than Object.</summary>
    public const string TypeAlreadyHasBase = "3073";

    /// <summary>Member cannot be moved to base class.</summary>
    public const string MemberNotMoveable = "3074";

    // --------------------------------------------
    // Async Conversion Errors (3080-3089)
    // --------------------------------------------

    /// <summary>Method is already async.</summary>
    public const string AlreadyAsync = "3080";

    /// <summary>Method has no awaitable calls to convert.</summary>
    public const string NoAsyncCalls = "3081";

    /// <summary>Cannot convert iterator method to async.</summary>
    public const string CannotConvertIterator = "3082";

    // --------------------------------------------
    // Inline/Variable Errors (3090-3099)
    // --------------------------------------------

    /// <summary>Variable has multiple assignments.</summary>
    public const string MultipleAssignments = "3090";

    /// <summary>Variable is used in ref/out context.</summary>
    public const string UsedInRefContext = "3091";

    /// <summary>Expression type is void.</summary>
    public const string ExpressionIsVoid = "3092";

    /// <summary>Cannot inline expression with side effects.</summary>
    public const string CannotInlineSideEffects = "3093";

    // ============================================
    // System Errors (4xxx)
    // ============================================

    /// <summary>Another operation in progress.</summary>
    public const string WorkspaceBusy = "4001";

    /// <summary>Cannot read/write files.</summary>
    public const string FilesystemError = "4002";

    /// <summary>Roslyn operation failed unexpectedly.</summary>
    public const string RoslynError = "4003";

    /// <summary>Changes would break compilation.</summary>
    public const string CompilationError = "4004";

    /// <summary>Operation exceeded time limit.</summary>
    public const string Timeout = "4005";

    // ============================================
    // Environment Errors (5xxx)
    // ============================================

    /// <summary>MSBuild not available.</summary>
    public const string MsBuildNotFound = "5001";

    /// <summary>Roslyn assemblies not loadable.</summary>
    public const string RoslynNotAvailable = "5002";

    /// <summary>.NET SDK not found.</summary>
    public const string SdkNotFound = "5003";

    /// <summary>Could not load solution.</summary>
    public const string SolutionLoadFailed = "5004";
}

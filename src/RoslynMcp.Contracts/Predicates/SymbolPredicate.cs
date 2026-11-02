using System.Text.Json.Serialization;
using RoslynMcp.Contracts.Coordinates;
using RoslynMcp.Contracts.Enums;

namespace RoslynMcp.Contracts.Predicates;

/// <summary>
/// Inclusive integer range <c>[Min, Max]</c> for count-based symbol predicates.
/// </summary>
/// <param name="Min">Optional inclusive minimum bound.</param>
/// <param name="Max">Optional inclusive maximum bound.</param>
public sealed record IntRange(
    [property: JsonPropertyName("min")] int? Min = null,
    [property: JsonPropertyName("max")] int? Max = null)
{
    /// <summary>
    /// Determines whether <paramref name="value"/> falls within <c>[Min, Max]</c>.
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <returns><c>true</c> if within bounds; otherwise <c>false</c>.</returns>
    public bool Contains(int value)
    {
        if (Min.HasValue && value < Min.Value)
        {
            return false;
        }

        if (Max.HasValue && value > Max.Value)
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Composable Layer 2 predicate AST for querying and filtering Roslyn symbols across a solution.
/// Multiple non-null leaf properties on the same node are evaluated conjunctively (logical AND).
/// </summary>
public sealed record SymbolPredicate
{
    /// <summary>
    /// Logical conjunction: all child predicates must match.
    /// </summary>
    [JsonPropertyName("all")]
    public IReadOnlyList<SymbolPredicate>? All { get; init; }

    /// <summary>
    /// Logical disjunction: at least one child predicate must match.
    /// </summary>
    [JsonPropertyName("any")]
    public IReadOnlyList<SymbolPredicate>? Any { get; init; }

    /// <summary>
    /// Logical negation: the child predicate must not match.
    /// </summary>
    [JsonPropertyName("not")]
    public SymbolPredicate? Not { get; init; }

    /// <summary>
    /// Allowed symbol kinds (e.g. <c>Class</c>, <c>Record</c>, <c>Interface</c>, <c>Struct</c>,
    /// <c>Enum</c>, <c>Delegate</c>, <c>Method</c>, <c>Property</c>, <c>Field</c>, <c>Event</c>, <c>Constant</c>).
    /// </summary>
    [JsonPropertyName("symbolKindIn")]
    public IReadOnlyList<string>? SymbolKindIn { get; init; }

    /// <summary>
    /// Allowed declared accessibilities (e.g. <c>Public</c>, <c>Internal</c>, <c>Protected</c>, <c>Private</c>).
    /// </summary>
    [JsonPropertyName("accessibilityIn")]
    public IReadOnlyList<string>? AccessibilityIn { get; init; }

    /// <summary>
    /// Symbol name pattern (supports wildcard glob <c>*</c>/<c>?</c>, regex <c>/pattern/</c> or <c>regex:pattern</c>, or exact/substring match).
    /// </summary>
    [JsonPropertyName("nameMatches")]
    public string? NameMatches { get; init; }

    /// <summary>
    /// Containing namespace pattern (supports hierarchical prefix, wildcard glob <c>*</c>/<c>**</c>, or regex <c>/pattern/</c>).
    /// </summary>
    [JsonPropertyName("namespaceMatches")]
    public string? NamespaceMatches { get; init; }

    /// <summary>
    /// Attribute name (with or without <c>Attribute</c> suffix, or fully qualified name) that must decorate the symbol.
    /// </summary>
    [JsonPropertyName("hasAttribute")]
    public string? HasAttribute { get; init; }

    /// <summary>
    /// Base type or interface name (simple or fully qualified) that the symbol or its containing/declared type must implement or inherit.
    /// </summary>
    [JsonPropertyName("implementsOrInherits")]
    public string? ImplementsOrInherits { get; init; }

    /// <summary>
    /// Inclusive bounds on the number of distinct caller locations referencing the symbol.
    /// </summary>
    [JsonPropertyName("callerCountRange")]
    public IntRange? CallerCountRange { get; init; }

    /// <summary>
    /// Inclusive bounds on the number of reference locations to the symbol across the solution.
    /// </summary>
    [JsonPropertyName("referencesCountRange")]
    public IntRange? ReferencesCountRange { get; init; }

    /// <summary>
    /// Document scope filter restricting where matching symbols may be declared.
    /// </summary>
    [JsonPropertyName("inDocumentScope")]
    public DocumentScope? InDocumentScope { get; init; }

    /// <summary>
    /// Creates a conjunctive (<c>All</c>) predicate from child predicates.
    /// </summary>
    /// <param name="predicates">Child predicates that must all match.</param>
    /// <returns>A composite <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate And(params SymbolPredicate[] predicates) =>
        new() { All = predicates };

    /// <summary>
    /// Creates a disjunctive (<c>Any</c>) predicate from child predicates.
    /// </summary>
    /// <param name="predicates">Child predicates where at least one must match.</param>
    /// <returns>A composite <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate Or(params SymbolPredicate[] predicates) =>
        new() { Any = predicates };

    /// <summary>
    /// Creates a negated (<c>Not</c>) predicate.
    /// </summary>
    /// <param name="predicate">Predicate to negate.</param>
    /// <returns>A negated <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate Negate(SymbolPredicate predicate) =>
        new() { Not = predicate };

    /// <summary>
    /// Creates a predicate matching any of the specified symbol kinds.
    /// </summary>
    /// <param name="kinds">Allowed symbol kinds.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithKinds(params SymbolKind[] kinds) =>
        new() { SymbolKindIn = kinds.Select(k => k.ToString()).ToArray() };

    /// <summary>
    /// Creates a predicate matching any of the specified symbol kind names.
    /// </summary>
    /// <param name="kinds">Allowed symbol kind names.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithKindNames(params string[] kinds) =>
        new() { SymbolKindIn = kinds };

    /// <summary>
    /// Creates a predicate matching any of the specified declared accessibilities.
    /// </summary>
    /// <param name="accessibilities">Allowed accessibility names.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithAccessibilities(params string[] accessibilities) =>
        new() { AccessibilityIn = accessibilities };

    /// <summary>
    /// Creates a predicate matching symbol names against <paramref name="pattern"/>.
    /// </summary>
    /// <param name="pattern">Glob, regex, or name pattern.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithName(string pattern) =>
        new() { NameMatches = pattern };

    /// <summary>
    /// Creates a predicate matching symbol namespaces against <paramref name="pattern"/>.
    /// </summary>
    /// <param name="pattern">Namespace prefix, glob, or regex pattern.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithNamespace(string pattern) =>
        new() { NamespaceMatches = pattern };

    /// <summary>
    /// Creates a predicate matching symbols decorated with <paramref name="attributeName"/>.
    /// </summary>
    /// <param name="attributeName">Attribute short or full name.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithAttribute(string attributeName) =>
        new() { HasAttribute = attributeName };

    /// <summary>
    /// Creates a predicate matching types/members implementing or inheriting <paramref name="typeOrInterfaceName"/>.
    /// </summary>
    /// <param name="typeOrInterfaceName">Interface or base type name.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate ImplementingOrInheriting(string typeOrInterfaceName) =>
        new() { ImplementsOrInherits = typeOrInterfaceName };

    /// <summary>
    /// Creates a predicate constraining caller count within <c>[min, max]</c>.
    /// </summary>
    /// <param name="min">Minimum caller count.</param>
    /// <param name="max">Maximum caller count.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithCallerCount(int? min = null, int? max = null) =>
        new() { CallerCountRange = new IntRange(min, max) };

    /// <summary>
    /// Creates a predicate constraining reference count within <c>[min, max]</c>.
    /// </summary>
    /// <param name="min">Minimum reference count.</param>
    /// <param name="max">Maximum reference count.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithReferenceCount(int? min = null, int? max = null) =>
        new() { ReferencesCountRange = new IntRange(min, max) };

    /// <summary>
    /// Creates a predicate restricting matched symbols to <paramref name="scope"/>.
    /// </summary>
    /// <param name="scope">Document scope filter.</param>
    /// <returns>A configured <see cref="SymbolPredicate"/>.</returns>
    public static SymbolPredicate WithinScope(DocumentScope scope) =>
        new() { InDocumentScope = scope };
}

/// <summary>
/// Request payload for the <c>roslyn_query_symbols</c> tool.
/// </summary>
public sealed record QuerySymbolsRequest
{
    /// <summary>
    /// Path to the <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file.
    /// </summary>
    [JsonPropertyName("solutionPath")]
    public string? SolutionPath { get; init; }

    /// <summary>
    /// Composable symbol predicate tree to evaluate.
    /// </summary>
    [JsonPropertyName("predicate")]
    public required SymbolPredicate Predicate { get; init; }

    /// <summary>
    /// Maximum number of matched symbols to return (defaults to 200).
    /// </summary>
    [JsonPropertyName("maxResults")]
    public int MaxResults { get; init; } = 200;
}

/// <summary>
/// Summary descriptor of a symbol matched by <see cref="SymbolPredicate"/>.
/// </summary>
public sealed record MatchedSymbolInfo
{
    /// <summary>Simple name of the symbol.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Fully qualified name of the symbol.</summary>
    [JsonPropertyName("fullyQualifiedName")]
    public required string FullyQualifiedName { get; init; }

    /// <summary>Mapped symbol kind name (e.g. <c>Class</c>, <c>Method</c>, <c>Property</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Declared accessibility (e.g. <c>Public</c>, <c>Internal</c>, <c>Private</c>).</summary>
    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; init; }

    /// <summary>Containing namespace name (empty string for global namespace).</summary>
    [JsonPropertyName("namespace")]
    public required string Namespace { get; init; }

    /// <summary>Project name containing the declaration.</summary>
    [JsonPropertyName("projectName")]
    public required string ProjectName { get; init; }

    /// <summary>Source file path where the symbol is declared.</summary>
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    /// <summary>1-based declaration start line.</summary>
    [JsonPropertyName("line")]
    public required int Line { get; init; }

    /// <summary>1-based declaration start column.</summary>
    [JsonPropertyName("column")]
    public required int Column { get; init; }
}

/// <summary>
/// Result returned from evaluating a <see cref="SymbolPredicate"/> across a solution.
/// </summary>
public sealed record SymbolQueryResult
{
    /// <summary>Whether the query succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>Matched symbols up to <c>MaxResults</c>.</summary>
    [JsonPropertyName("symbols")]
    public required IReadOnlyList<MatchedSymbolInfo> Symbols { get; init; }

    /// <summary>Total number of matched symbols before truncation.</summary>
    [JsonPropertyName("totalCount")]
    public required int TotalCount { get; init; }

    /// <summary>Whether results were truncated by <c>MaxResults</c>.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>Execution time in milliseconds.</summary>
    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; init; }

    /// <summary>Error message if <see cref="Success"/> is <c>false</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

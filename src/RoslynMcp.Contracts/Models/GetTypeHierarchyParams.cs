namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the get_type_hierarchy query.
/// </summary>
public sealed class GetTypeHierarchyParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type symbol.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for position-based resolution.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Direction: Ancestors, Descendants, or Both. Default: Both.
    /// </summary>
    public string? Direction { get; init; }

    /// <summary>
    /// Maximum number of derived types to return. Omit for no cap (return all derived types).
    /// Does not cap BaseTypes or Interfaces.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported derived types to. When set, only
    /// derived types declared (with an in-source location) in that file are returned in
    /// <c>DerivedTypes</c>, before <see cref="MaxResults"/>, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. Does not filter BaseTypes or Interfaces.
    /// When omitted, derived types across the whole solution are returned. Distinct from
    /// <see cref="SourceFile"/>, which locates the target type.
    /// </summary>
    public string? DerivedFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported
    /// derived types to. When set, only derived types with an in-source location whose document
    /// belongs to a project whose file path equals it are returned in <c>DerivedTypes</c>, reported
    /// at that location (every target-framework variant of a multi-targeted project shares that path,
    /// so all are included); same validation and matching as <c>get_diagnostics</c> /
    /// <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c> /
    /// <c>find_implementations</c> <c>projectPath</c>, and combined with <see cref="DerivedFile"/>.
    /// Applied before <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// project-scoped set. Does not filter BaseTypes or Interfaces. When omitted or blank, derived
    /// types across the whole solution are returned. <see cref="SourceFile"/> still locates the
    /// target type and may be in another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Whether <c>DerivedTypes</c> includes every descendant (<c>true</c>, the default) or only
    /// direct subclasses (<c>false</c>): types whose immediate base type is the target type, via
    /// Roslyn <c>SymbolFinder.FindDerivedClassesAsync(..., transitive: false)</c>. When
    /// <c>false</c>, <see cref="DerivedFile"/> and <see cref="ProjectPath"/> filter the direct set
    /// (so a type in the scoped project that derives only through an intermediate type is not
    /// reported) and <see cref="MaxResults"/> is applied afterwards, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the direct set. Does not affect BaseTypes or Interfaces, and has no
    /// effect when <c>direction</c> is <c>Ancestors</c>. Omit or <c>true</c> for today's behavior.
    /// </summary>
    public bool? Transitive { get; init; }
}

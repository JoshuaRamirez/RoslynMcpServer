namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding implementations of an interface or abstract member.
/// </summary>
public sealed class FindImplementationsParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the interface, abstract class, or virtual member.
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
    /// Maximum number of implementations to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported implementations to. When set, only
    /// implementations declared (with an in-source location) in that file are returned, before
    /// <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set.
    /// When omitted, implementations across the whole solution are returned. Distinct from
    /// <see cref="SourceFile"/>, which locates the target symbol.
    /// </summary>
    public string? ImplementationFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported
    /// implementations to. When set, only implementations with an in-source location whose document
    /// belongs to a project whose file path equals it are returned, reported at that location (every
    /// target-framework variant of a multi-targeted project shares that path, so all are included);
    /// same validation and matching as <c>get_diagnostics</c> / <c>search_symbols</c> /
    /// <c>find_references</c> / <c>find_callers</c> <c>projectPath</c>, and combined with
    /// <see cref="ImplementationFile"/>. Applied before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set. When omitted or blank,
    /// implementations across the whole solution are returned. <see cref="SourceFile"/> still
    /// locates the target symbol and may be in another project.
    /// </summary>
    public string? ProjectPath { get; init; }

    /// <summary>
    /// Optional: when the target symbol is an interface type, whether to report every implementing
    /// class / struct (<c>true</c>, the default and today's behavior) or only those that implement the
    /// interface directly (<c>false</c>), via Roslyn's
    /// <c>SymbolFinder.FindImplementationsAsync(INamedTypeSymbol, Solution, transitive: false, ...)</c>:
    /// types that only inherit the implementation from a base class, or that implement it through a
    /// derived interface, are then omitted. Same idea as <c>get_type_hierarchy</c> <c>transitive</c>.
    /// <see cref="ImplementationFile"/> / <see cref="ProjectPath"/> filter, and
    /// <see cref="MaxResults"/> caps, the resulting set, so <c>TotalCount</c> / <c>Truncated</c>
    /// reflect it. No effect for member targets or non-interface types. When omitted, <c>true</c>.
    /// </summary>
    public bool? Transitive { get; init; }
}

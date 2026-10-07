namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the find_callers query.
/// </summary>
public sealed class FindCallersParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find callers for.
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
    /// Maximum number of callers to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported call sites to. When set, only
    /// call sites located in that file are returned (before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the filtered set). When omitted, call
    /// sites across the whole solution are returned. Distinct from <see cref="SourceFile"/>,
    /// which locates the target symbol.
    /// </summary>
    public string? CallerFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported call
    /// sites to. When set, only call sites whose document belongs to a project whose file path equals
    /// it are returned (every target-framework variant of a multi-targeted project shares that path,
    /// so all are included); same validation and matching as <c>get_diagnostics</c> /
    /// <c>search_symbols</c> / <c>find_references</c> <c>projectPath</c>, and a path matching no
    /// project is an error rather than an empty result. Omit or leave blank for the whole solution.
    /// Applied together with <see cref="CallerFile"/> and before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set. <see cref="SourceFile"/>
    /// still locates the target symbol and may live in another project.
    /// </summary>
    public string? ProjectPath { get; init; }
}

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for finding all references to a symbol.
/// </summary>
public sealed class FindReferencesParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find references for.
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
    /// Maximum number of references to return.
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Whether to include the symbol's own definition locations (<c>IsDefinition = true</c>).
    /// Omit or <c>true</c> for today's behavior; <c>false</c> returns usages only, applied before
    /// <see cref="MaxResults"/> so <c>TotalCount</c> / <c>Truncated</c> reflect the usage-only set.
    /// </summary>
    public bool? IncludeDeclaration { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported reference locations to. When set, only
    /// locations (usages and, when <see cref="IncludeDeclaration"/> is on, definition locations)
    /// located in that file are returned, before <see cref="MaxResults"/>, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. When omitted, locations across the whole solution
    /// are returned. Distinct from <see cref="SourceFile"/>, which locates the target symbol.
    /// </summary>
    public string? ReferenceFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported reference
    /// locations to. When set, only locations (usages and, when <see cref="IncludeDeclaration"/> is on,
    /// definition locations) whose document belongs to a project whose file path equals it are returned
    /// (every target-framework variant of a multi-targeted project shares that path, so all are
    /// included); same validation and matching as <c>get_diagnostics</c> / <c>search_symbols</c>
    /// <c>projectPath</c>, and a path matching no project is an error rather than an empty result.
    /// Omit or leave blank for the whole solution. Applied together with <see cref="ReferenceFile"/>
    /// and before <see cref="MaxResults"/>, so <c>TotalCount</c> / <c>Truncated</c> reflect the
    /// project-scoped set. <see cref="SourceFile"/> still locates the target symbol and may live in
    /// another project.
    /// </summary>
    public string? ProjectPath { get; init; }
}

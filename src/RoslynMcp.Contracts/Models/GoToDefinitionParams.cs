namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for navigating to a symbol's definition.
/// </summary>
public sealed class GoToDefinitionParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol reference.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the symbol to find the definition of.
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
    /// Maximum number of definition locations to return. Omit for no cap (return all definitions).
    /// </summary>
    public int? MaxResults { get; init; }

    /// <summary>
    /// Absolute path to a source file to restrict reported definition locations to. When set,
    /// only definition locations whose path equals that file are returned in
    /// <c>Definitions</c>, before <see cref="MaxResults"/>, so <c>TotalCount</c> /
    /// <c>Truncated</c> reflect the filtered set. Metadata (non-source) definitions have no
    /// file and are excluded when this is set. For a partial method / property / event, the
    /// locations of both the defining declaration and the implementation are considered, so
    /// the other half's file can be selected regardless of which half the symbol resolved to.
    /// When omitted, the existing unfiltered behavior is preserved. Distinct from <see cref="SourceFile"/>, which locates the symbol.
    /// </summary>
    public string? DefinitionFile { get; init; }

    /// <summary>
    /// Optional absolute path to a <c>.csproj</c> in the loaded workspace to restrict reported
    /// definition locations to. When set, only in-source definition locations whose document
    /// belongs to a project whose file path equals it are returned in <c>Definitions</c> (every
    /// target-framework variant of a multi-targeted project shares that path, so all are included);
    /// same validation and matching as <c>get_diagnostics</c> / <c>search_symbols</c> /
    /// <c>find_references</c> / <c>find_callers</c> / <c>find_implementations</c> /
    /// <c>get_type_hierarchy</c> <c>projectPath</c>, and combined with <see cref="DefinitionFile"/>
    /// (a location must satisfy both). Applied before <see cref="MaxResults"/>, so
    /// <c>TotalCount</c> / <c>Truncated</c> reflect the project-scoped set. Metadata (non-source)
    /// definitions have no project document and are excluded when this is set. When omitted or
    /// blank, the existing unfiltered behavior is preserved. <see cref="SourceFile"/> still locates
    /// the symbol and may be in another project.
    /// </summary>
    public string? ProjectPath { get; init; }
}

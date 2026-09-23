namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_symbol tool.
/// </summary>
public sealed class RenameSymbolParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible declaration in every C# document
    /// (or the optional single <see cref="SourceFile"/>) whose simple name
    /// equals <see cref="SymbolName"/>, renaming each to <see cref="NewName"/>
    /// under today's single-site validation.
    /// When true, cannot be combined with <see cref="Line"/> or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Current name of the symbol to rename.
    /// Required for both single-site and allFiles.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// New name for the symbol.
    /// Required for both single-site and allFiles.
    /// </summary>
    public required string NewName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for disambiguation. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Whether to rename all overloads of a method. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool RenameOverloads { get; init; }

    /// <summary>
    /// Whether to rename interface implementations. Default: true.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool RenameImplementations { get; init; } = true;

    /// <summary>
    /// Whether to rename the file if renaming a type. Default: true.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool RenameFile { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

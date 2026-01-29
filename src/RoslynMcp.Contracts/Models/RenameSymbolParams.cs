namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_symbol tool.
/// </summary>
public sealed class RenameSymbolParams
{
    /// <summary>
    /// Absolute path to the source file containing the symbol.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Current name of the symbol to rename.
    /// </summary>
    public required string SymbolName { get; init; }

    /// <summary>
    /// New name for the symbol.
    /// </summary>
    public required string NewName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number for disambiguation.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Whether to rename all overloads of a method. Default: false.
    /// </summary>
    public bool RenameOverloads { get; init; }

    /// <summary>
    /// Whether to rename interface implementations. Default: true.
    /// </summary>
    public bool RenameImplementations { get; init; } = true;

    /// <summary>
    /// Whether to rename the file if renaming a type. Default: true.
    /// </summary>
    public bool RenameFile { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

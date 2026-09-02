namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_file_to_match_type tool.
/// </summary>
public sealed class RenameFileToMatchTypeParams
{
    /// <summary>
    /// Absolute path to the source file to rename.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single file.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="TypeName"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Type name used to disambiguate when the file declares more than one type.
    /// Single-site only.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number used to select a type when the file declares more than one.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number used with <see cref="Line"/> when multiple types share a line.
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

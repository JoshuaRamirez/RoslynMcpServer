namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the rename_file_to_match_type tool.
/// </summary>
public sealed class RenameFileToMatchTypeParams
{
    /// <summary>
    /// Absolute path to the source file to rename.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Type name used to disambiguate when the file declares more than one type.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number used to select a type when the file declares more than one.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column number used with <see cref="Line"/> when multiple types share a line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

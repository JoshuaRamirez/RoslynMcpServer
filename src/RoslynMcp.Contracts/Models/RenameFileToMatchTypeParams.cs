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
    /// When <see cref="Column"/> is omitted, matching stays today's covering-span line pick.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the smallest type whose identifier or declaration span covers
    /// that column (identifier preferred, then smallest covering declaration).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's omitted-line path.
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

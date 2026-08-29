namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_to_block_body tool.
/// </summary>
public sealed class ConvertToBlockBodyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the member to convert.
    /// </summary>
    public string? MemberName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one member shares a
    /// line, or when the identifier lives on a continuation line of a split
    /// signature. Optional. When set, selects the member whose identifier
    /// or declaration span covers that column (using <see cref="Line"/> when
    /// present). Omitted keeps today's memberName and/or line pick (smallest
    /// containing node).
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

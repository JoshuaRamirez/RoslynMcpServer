namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_property tool.
/// </summary>
public sealed class ConvertPropertyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the property to convert.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation when more than one property shares a
    /// line, or when the identifier lives on a continuation line of a split
    /// declaration. Optional. When set, selects the smallest property whose
    /// identifier or declaration span covers that column (using
    /// <see cref="Line"/> and/or <see cref="PropertyName"/> when present).
    /// Do not require the declaration to start on <see cref="Line"/> when
    /// this is set. Omitted keeps today's propertyName and/or line start-line
    /// pick so indented properties still convert.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Conversion direction: ToAutoProperty or ToFullProperty.
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

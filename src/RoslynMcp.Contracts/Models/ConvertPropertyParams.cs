namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_property tool.
/// </summary>
public sealed class ConvertPropertyParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single property.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="PropertyName"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the property to convert. Single-site only.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// 1-based line number for position-based resolution. Single-site only.
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
    /// pick so indented properties still convert. Single-site only.
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

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
    /// Conversion direction: ToAutoProperty or ToFullProperty.
    /// </summary>
    public required string Direction { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_anonymous_to_class tool.
/// </summary>
public sealed class ConvertAnonymousToClassParams
{
    /// <summary>
    /// Absolute path to the source file containing the anonymous object creation.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line number of the anonymous object creation (<c>new { ... }</c>).
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// Name of the class or record to create.
    /// </summary>
    public required string NewTypeName { get; init; }

    /// <summary>
    /// 1-based column number for disambiguation when multiple anonymous creations share a line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Create a <c>record</c> instead of a <c>class</c>. Default: false.
    /// </summary>
    public bool AsRecord { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_base_class tool.
/// </summary>
public sealed class ExtractBaseClassParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to extract base class from.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Name for the new base class.
    /// </summary>
    public required string BaseClassName { get; init; }

    /// <summary>
    /// Names of members to move to base class.
    /// </summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// Absolute path for the base class file. If null, creates in same file.
    /// </summary>
    public string? TargetFile { get; init; }

    /// <summary>
    /// Make base class abstract. Default: false.
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

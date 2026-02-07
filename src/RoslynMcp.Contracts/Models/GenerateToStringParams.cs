namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_tostring tool.
/// </summary>
public sealed class GenerateToStringParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate ToString for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Specific field/property names to include. If null, uses all fields and properties.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// Format: "interpolated" (default) or "stringbuilder".
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

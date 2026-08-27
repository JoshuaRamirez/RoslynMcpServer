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
    /// When <see cref="IncludeInheritedMembers"/> is true, auto-collection and name
    /// resolution also consider accessible inherited members.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// Format: "interpolated" (default) or "stringbuilder". Case-insensitive.
    /// Omitted, null, or empty uses interpolated. Unknown values are rejected.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// When true, also collect accessible instance fields and readable properties
    /// declared on base types until <c>System.Object</c> / <c>System.ValueType</c>.
    /// Only members visible from this type are included (public / protected /
    /// protected-internal; internal when same assembly).
    /// Default: false (this-type-only collection).
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

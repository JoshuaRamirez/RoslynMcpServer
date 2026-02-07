namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_equals_hashcode tool.
/// </summary>
public sealed class GenerateEqualsHashCodeParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate Equals/GetHashCode for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Specific field/property names to include. If null, uses all fields and properties.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

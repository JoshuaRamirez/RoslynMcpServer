namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the encapsulate_field tool.
/// </summary>
public sealed class EncapsulateFieldParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the field to encapsulate.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Name for the property. If null, derives from field name.
    /// </summary>
    public string? PropertyName { get; init; }

    /// <summary>
    /// Create read-only property (getter only). Default: false.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

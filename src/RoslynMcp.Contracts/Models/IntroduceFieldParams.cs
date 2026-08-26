namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the introduce_field tool.
/// </summary>
public sealed class IntroduceFieldParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Start line of the local variable or expression (1-based).
    /// </summary>
    public required int StartLine { get; init; }

    /// <summary>
    /// Start column of the local variable or expression (1-based).
    /// </summary>
    public required int StartColumn { get; init; }

    /// <summary>
    /// End line of the local variable or expression (1-based).
    /// </summary>
    public required int EndLine { get; init; }

    /// <summary>
    /// End column of the local variable or expression (1-based).
    /// </summary>
    public required int EndColumn { get; init; }

    /// <summary>
    /// Name for the new field.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Create as a readonly field. Default: false.
    /// </summary>
    public bool IsReadonly { get; init; }

    /// <summary>
    /// Create as a static field. Default: false.
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Initialize the field in a constructor instead of inline. Default: false.
    /// </summary>
    public bool InitializeInConstructor { get; init; }

    /// <summary>
    /// Replace all identical expressions in the containing type. Default: false.
    /// </summary>
    public bool ReplaceAll { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the introduce_field tool.
/// </summary>
public sealed class IntroduceFieldParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible promotable local in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="StartLine"/>,
    /// <see cref="StartColumn"/>, <see cref="EndLine"/>,
    /// <see cref="EndColumn"/>, or <see cref="FieldName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Start line of the local variable or expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// Start column of the local variable or expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// End line of the local variable or expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// End column of the local variable or expression (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndColumn { get; init; }

    /// <summary>
    /// Name for the new field. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, each field is named from its local
    /// (preserving <c>@</c>-escape / keyword rules).
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Create as a readonly field. Default: false.
    /// Valid with <see cref="AllFiles"/> (shared option; skip sites that cannot honor it).
    /// </summary>
    public bool IsReadonly { get; init; }

    /// <summary>
    /// Create as a static field. Default: false.
    /// Valid with <see cref="AllFiles"/> (shared option; skip sites that cannot honor it).
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Initialize the field in a constructor instead of inline. Default: false.
    /// Valid with <see cref="AllFiles"/> (shared option; skip sites that cannot honor it).
    /// </summary>
    public bool InitializeInConstructor { get; init; }

    /// <summary>
    /// Replace all identical expressions in the containing type. Default: false.
    /// Expression-only sites are skipped in <see cref="AllFiles"/> walks;
    /// this remains valid for single-site and as a shared option where it applies.
    /// </summary>
    public bool ReplaceAll { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

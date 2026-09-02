namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the make_static tool.
/// </summary>
public sealed class MakeStaticParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible ordinary instance method in every
    /// C# document (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="StartLine"/>,
    /// <see cref="StartColumn"/>, <see cref="EndLine"/>,
    /// <see cref="EndColumn"/>, or <see cref="SymbolName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Start line of the selected method (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// Start column of the selected method (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// End line of the selected method (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// End column of the selected method (1-based). Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndColumn { get; init; }

    /// <summary>
    /// Optional method name used to confirm the selection. Single-site only.
    /// </summary>
    public string? SymbolName { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

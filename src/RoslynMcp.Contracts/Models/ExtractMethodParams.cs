namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the extract_method tool.
/// </summary>
public sealed class ExtractMethodParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible contiguous statement run in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="StartLine"/>,
    /// <see cref="StartColumn"/>, <see cref="EndLine"/>,
    /// <see cref="EndColumn"/>, or <see cref="MethodName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based start line of selection. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// 1-based start column of selection. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    /// 1-based end line of selection. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// 1-based end column of selection. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public int? EndColumn { get; init; }

    /// <summary>
    /// Name for the new method. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, each method is named from its
    /// statement text (sanitized to a valid PascalCase identifier).
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Visibility for the new method (private, internal, protected, public). Default: private.
    /// Valid with <see cref="AllFiles"/> (shared option).
    /// </summary>
    public string Visibility { get; init; } = "private";

    /// <summary>
    /// Force the method to be static. Default: false (auto-detect).
    /// Valid with <see cref="AllFiles"/> (shared option).
    /// </summary>
    public bool? MakeStatic { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

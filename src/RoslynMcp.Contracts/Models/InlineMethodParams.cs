namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_method tool.
/// </summary>
public sealed class InlineMethodParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible method in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="MethodName"/>,
    /// <see cref="Line"/>, <see cref="Column"/>, or <see cref="CallSiteLocation"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the method to inline. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// Line number of the method declaration (1-based). Optional for disambiguation.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the smallest method
    /// whose identifier or declaration span covers that column. Omitted keeps
    /// today's MethodName and/or Line identifier start-line pick. Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// When set, inline only this call site and leave the method in place.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public CallSiteLocation? CallSiteLocation { get; init; }

    /// <summary>
    /// Remove the method after inlining all call sites. Default: true.
    /// Ignored when <see cref="CallSiteLocation"/> is set.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool RemoveMethod { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

/// <summary>
/// Identifies a single call site for partial inlining.
/// </summary>
public sealed class CallSiteLocation
{
    /// <summary>
    /// Absolute path to the file containing the call site.
    /// </summary>
    public required string File { get; init; }

    /// <summary>
    /// 1-based line of the call site.
    /// </summary>
    public required int Line { get; init; }

    /// <summary>
    /// 1-based column of the call site.
    /// </summary>
    public required int Column { get; init; }
}

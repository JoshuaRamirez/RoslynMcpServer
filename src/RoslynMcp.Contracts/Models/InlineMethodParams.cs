namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the inline_method tool.
/// </summary>
public sealed class InlineMethodParams
{
    /// <summary>
    /// Absolute path to the source file containing the method.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the method to inline.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Line number of the method declaration (1-based). Optional for disambiguation.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// Column number of the method declaration (1-based). Optional for disambiguation.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// When set, inline only this call site and leave the method in place.
    /// </summary>
    public CallSiteLocation? CallSiteLocation { get; init; }

    /// <summary>
    /// Remove the method after inlining all call sites. Default: true.
    /// Ignored when <see cref="CallSiteLocation"/> is set.
    /// </summary>
    public bool RemoveMethod { get; init; } = true;

    /// <summary>
    /// Return computed changes without applying. Default: false.
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

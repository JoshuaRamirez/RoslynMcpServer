namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the remove_braces tool.
/// </summary>
public sealed class RemoveBracesParams
{
    /// <summary>
    /// Absolute path to the source file. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single file.
    /// When true, <see cref="SourceFile"/> is optional. Omitted <see cref="Scope"/>
    /// uses file-scope. Cannot be combined with <see cref="Scope"/> <c>statement</c>
    /// or <c>type</c>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line of the control-statement keyword. Required when
    /// <see cref="Scope"/> is <c>statement</c>.
    /// When <see cref="Column"/> is omitted, matching stays today's first
    /// / leftmost keyword on the line by <c>SpanStart</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column on the control-statement keyword. When set with
    /// <see cref="Line"/>, selects the statement whose keyword span covers
    /// that column (exclusive-end; today's shortest keyword / First among
    /// covering). Omitted keeps today's first/leftmost-keyword-on-line-by-
    /// <c>SpanStart</c> pick. Column without line keeps today's required-line
    /// validation.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Scope of the operation: <c>statement</c> (default for single-file),
    /// <c>file</c>, or <c>type</c>. When <see cref="AllFiles"/> is true, omitted
    /// scope uses file-scope.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Type name when <see cref="Scope"/> is <c>type</c>.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

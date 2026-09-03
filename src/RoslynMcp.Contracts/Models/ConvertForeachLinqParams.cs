namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the convert_foreach_linq tool.
/// </summary>
public sealed class ConvertForeachLinqParams
{
    /// <summary>
    /// Absolute path to the source file. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single foreach.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="Line"/> or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line number of the foreach keyword.
    /// Required when <see cref="AllFiles"/> is false. Single-foreach only.
    /// When <see cref="Column"/> is omitted, matching stays today's first
    /// <c>ForEachKeyword</c> on the line by <c>SpanStart</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column on the foreach keyword. When set with <see cref="Line"/>,
    /// selects the foreach whose <c>ForEachKeyword</c> span covers that column
    /// (exclusive-end; today's FirstOrDefault among covering keywords).
    /// Omitted keeps today's first-<c>ForEachKeyword</c>-on-line-by-<c>SpanStart</c> pick.
    /// Column without line keeps today's required-line validation.
    /// Single-foreach only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Emit query syntax (<c>from … where … select</c>) instead of method
    /// syntax (<c>.Where().Select()</c>). Default: false (today's method
    /// syntax). Patterns without a query-syntax form (Any / All /
    /// FirstOrDefault / Count / Sum) keep method syntax.
    /// </summary>
    public bool PreferQuerySyntax { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

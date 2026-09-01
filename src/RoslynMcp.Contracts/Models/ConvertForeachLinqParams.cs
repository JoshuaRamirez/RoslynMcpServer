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
    /// 1-based line number of the foreach statement to convert.
    /// Required when <see cref="AllFiles"/> is false. Single-foreach only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column of the foreach keyword. Optional. When set, selects the
    /// foreach whose keyword covers that column on <see cref="Line"/>. Spec
    /// default is 1; omitted keeps today's first-foreach-on-the-line pick so
    /// indented statements still convert without a column. Single-foreach only.
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

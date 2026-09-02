namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the add_null_checks tool.
/// </summary>
public sealed class AddNullChecksParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single method.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="MethodName"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the method/constructor to add null checks to. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? MethodName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set, selects the smallest method
    /// or constructor whose identifier or declaration span covers that column.
    /// Omitted keeps today's MethodName and optional Line start-line pick
    /// (including today's silent First() fallback when line misses).
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Style: "throw" (ArgumentNullException.ThrowIfNull) or "guard" (if-throw).
    /// Default: "throw".
    /// </summary>
    public string? Style { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

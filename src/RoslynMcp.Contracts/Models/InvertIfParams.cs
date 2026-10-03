namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the invert_if tool.
/// </summary>
public sealed class InvertIfParams
{
    /// <summary>
    /// Absolute path to the source file containing the if statement.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional — limits the walk to that one file.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single if.
    /// When true, <see cref="SourceFile"/> is optional (omit to walk the whole solution;
    /// supply to limit the walk via <c>FilterAllFilesDocumentsBySourceFile</c>).
    /// Cannot be combined with <see cref="Line"/> or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// 1-based line number of the if keyword.
    /// Required when <see cref="AllFiles"/> is false. Single-site only.
    /// When <see cref="Column"/> is omitted, matching stays today's first
    /// <c>IfKeyword</c> on the line by <c>SpanStart</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column on the if keyword. When set with <see cref="Line"/>,
    /// selects the if whose <c>IfKeyword</c> span covers that column
    /// (exclusive-end; today's FirstOrDefault among covering keywords).
    /// Omitted keeps today's first-<c>IfKeyword</c>-on-line-by-<c>SpanStart</c> pick.
    /// Column without line keeps today's required-line validation.
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

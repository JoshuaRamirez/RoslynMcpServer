namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the simplify_name tool (UC-A8).
/// </summary>
public sealed class SimplifyNameParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line of the qualified name. Required when
    /// <see cref="Scope"/> is <c>location</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column on the qualified name. Optional; used to
    /// narrow which name on <see cref="Line"/> is simplified.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Scope of the operation: <c>file</c> (default) or <c>location</c>.
    /// </summary>
    public string Scope { get; init; } = "file";

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

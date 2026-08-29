namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the remove_braces tool.
/// </summary>
public sealed class RemoveBracesParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 1-based line of the control-statement keyword. Required when
    /// <see cref="Scope"/> is <c>statement</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column on the control-statement keyword. Optional; used to
    /// disambiguate when multiple control statements share a line.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Scope of the operation: <c>statement</c> (default), <c>file</c>, or <c>type</c>.
    /// </summary>
    public string Scope { get; init; } = "statement";

    /// <summary>
    /// Type name when <see cref="Scope"/> is <c>type</c>.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

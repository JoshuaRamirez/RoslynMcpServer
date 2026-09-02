namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the use_base_type tool.
/// </summary>
public sealed class UseBaseTypeParams
{
    /// <summary>
    /// Absolute path to the source file containing the derived type.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process all C# documents in the solution instead of a single type.
    /// When true, <see cref="SourceFile"/> is optional. Cannot be combined with
    /// <see cref="TypeName"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// Bulk walks every eligible type; it does not broaden search for one
    /// <see cref="TypeName"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the derived class, struct, or interface whose references
    /// should be rewritten to a compatible base type. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName first-match
    /// on <c>TypeDeclarationSyntax</c> (simple name / single match) or FQN
    /// semantic <c>TypeNameMatches</c> narrowing. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's omitted-line <c>PickOmittedLineMatch</c> after
    /// the typeName filter (<c>TypeDeclarationSyntax</c> only).
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Target base class or interface name. If omitted, uses the nearest base
    /// class, or the single implemented interface when there is no class base.
    /// With <see cref="AllFiles"/>, acts as an optional filter: types that
    /// do not have this base are skipped.
    /// </summary>
    public string? TargetBaseType { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

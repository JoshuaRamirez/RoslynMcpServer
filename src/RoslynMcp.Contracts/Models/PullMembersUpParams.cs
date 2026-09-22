namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the pull_members_up tool.
/// </summary>
public sealed class PullMembersUpParams
{
    /// <summary>
    /// Absolute path to the source file containing the derived type.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible derived type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Line"/>, <see cref="Column"/>, <see cref="Members"/>,
    /// or <see cref="TargetBaseType"/>.
    /// Bulk auto-selects every pullable member and resolves the target the
    /// same way omitted <see cref="TargetBaseType"/> does today.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the derived class or struct that currently declares the members.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>TypeDeclarationSyntax</c>.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter
    /// (<c>TypeDeclarationSyntax</c> only).
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Names of members to pull up. At least one is required for single-site.
    /// Indexers match metadata name (<c>Item</c>), Roslyn name (<c>this[]</c>),
    /// and conventional display (<c>this[int i]</c>).
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// When <see cref="AllFiles"/> is true, every pullable member is selected.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Target base class or interface name. If omitted, uses the nearest base
    /// class, or the single implemented interface when there is no class base.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// Bulk always uses the omitted-<see cref="TargetBaseType"/> resolution.
    /// </summary>
    public string? TargetBaseType { get; init; }

    /// <summary>
    /// When pulling to a class, declare the members as abstract on the base
    /// and keep the original implementations as overrides on the derived type.
    /// Ignored when the target is an interface. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

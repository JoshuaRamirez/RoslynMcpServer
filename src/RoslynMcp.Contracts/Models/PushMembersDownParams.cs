namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the push_members_down tool.
/// </summary>
public sealed class PushMembersDownParams
{
    /// <summary>
    /// Absolute path to the source file containing the base type.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible base type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Line"/>, <see cref="Column"/>, <see cref="Members"/>,
    /// or <see cref="TargetDerivedTypes"/>.
    /// Bulk auto-selects every pushable member and pushes to every direct
    /// derived type (same as omitted <see cref="TargetDerivedTypes"/>).
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the base class or interface that currently declares the members.
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
    /// Names of members to push down. At least one is required for single-site.
    /// Indexers match metadata name (<c>Item</c>), Roslyn name (<c>this[]</c>),
    /// and conventional display (<c>this[int i]</c>).
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// When <see cref="AllFiles"/> is true, every pushable member is selected.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Specific derived type names to push to. If omitted or empty, pushes
    /// to every direct derived class, struct, or interface in the workspace.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// Bulk always uses the omitted-<see cref="TargetDerivedTypes"/> resolution.
    /// </summary>
    public IReadOnlyList<string>? TargetDerivedTypes { get; init; }

    /// <summary>
    /// Leave an abstract declaration on the source class and add overrides
    /// on the derived types. Ignored when the source is an interface
    /// (interface members always remain). Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool LeaveAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

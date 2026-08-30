namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the push_members_down tool.
/// </summary>
public sealed class PushMembersDownParams
{
    /// <summary>
    /// Absolute path to the source file containing the base type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the base class or interface that currently declares the members.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>TypeDeclarationSyntax</c>.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter
    /// (<c>TypeDeclarationSyntax</c> only).
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Names of members to push down. At least one is required. Indexers match
    /// metadata name (<c>Item</c>), Roslyn name (<c>this[]</c>), and
    /// conventional display (<c>this[int i]</c>).
    /// </summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// Specific derived type names to push to. If omitted or empty, pushes
    /// to every direct derived class, struct, or interface in the workspace.
    /// </summary>
    public IReadOnlyList<string>? TargetDerivedTypes { get; init; }

    /// <summary>
    /// Leave an abstract declaration on the source class and add overrides
    /// on the derived types. Ignored when the source is an interface
    /// (interface members always remain). Default: false.
    /// </summary>
    public bool LeaveAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

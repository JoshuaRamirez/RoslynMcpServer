namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the pull_members_up tool.
/// </summary>
public sealed class PullMembersUpParams
{
    /// <summary>
    /// Absolute path to the source file containing the derived type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the derived class or struct that currently declares the members.
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
    /// Names of members to pull up. At least one is required. Indexers match
    /// metadata name (<c>Item</c>), Roslyn name (<c>this[]</c>), and
    /// conventional display (<c>this[int i]</c>).
    /// </summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>
    /// Target base class or interface name. If omitted, uses the nearest base
    /// class, or the single implemented interface when there is no class base.
    /// </summary>
    public string? TargetBaseType { get; init; }

    /// <summary>
    /// When pulling to a class, declare the members as abstract on the base
    /// and keep the original implementations as overrides on the derived type.
    /// Ignored when the target is an interface. Default: false.
    /// </summary>
    public bool MakeAbstract { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

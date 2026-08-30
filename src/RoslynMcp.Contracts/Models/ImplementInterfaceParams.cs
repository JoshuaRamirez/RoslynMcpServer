namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the implement_interface tool.
/// </summary>
public sealed class ImplementInterfaceParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to implement interface on.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Name of the interface to implement (simple or fully qualified).
    /// </summary>
    public required string InterfaceName { get; init; }

    /// <summary>
    /// Use explicit interface implementation. Default: false.
    /// </summary>
    public bool ExplicitImplementation { get; init; }

    /// <summary>
    /// Names of specific members to implement. If null or empty, implements
    /// all missing members. When <see cref="ReplaceExisting"/> is true,
    /// omitted / empty also replaces every existing implementable member
    /// for that interface. A non-empty list is only those names (missing
    /// or existing). Indexers match metadata name (<c>Item</c>), Roslyn
    /// name (<c>this[]</c>), and conventional display (<c>this[int i]</c>).
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Throw NotImplementedException in method bodies. Default: true.
    /// </summary>
    public bool ThrowNotImplemented { get; init; } = true;

    /// <summary>
    /// When true, already-implemented interface members become eligible
    /// alongside missing members. Existing declarations (ordinary methods,
    /// properties, indexers, and events — including on other partials)
    /// that match a selected member by signature are removed and a standard
    /// generated stub is inserted. Match methods by name + parameter types
    /// in order + <c>RefKind</c>, properties and events by name, and
    /// indexers by the same identity forms as the <c>members</c> filter
    /// (metadata <c>Item</c>, <c>this[]</c>, display forms) plus parameter
    /// types and <c>RefKind</c>. When <see cref="ExplicitImplementation"/>
    /// is true, match/replace the explicit interface form; when false, the
    /// ordinary public stub. Two same-name existing implementations with
    /// no exact signature match fail with <c>NameCollision</c> — this flag
    /// does not guess. Property/event accessors are never treated as
    /// ordinary methods. Generated stubs still honor
    /// <see cref="ExplicitImplementation"/> and
    /// <see cref="ThrowNotImplemented"/>. Default: false (only unimplemented
    /// members; all-already-implemented still fails with
    /// <c>MemberAlreadyImplemented</c>).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

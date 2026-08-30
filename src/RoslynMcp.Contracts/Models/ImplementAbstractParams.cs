namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the implement_abstract tool.
/// </summary>
public sealed class ImplementAbstractParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the class to implement inherited abstract members on.
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
    /// Names of specific abstract members to implement. If null or empty,
    /// implements all missing members. When <see cref="ReplaceExisting"/> is
    /// true, omitted / empty also replaces every existing implementable
    /// abstract member this tool would emit. A non-empty list is only those
    /// names (missing or existing). Indexers match metadata name (<c>Item</c>),
    /// Roslyn name (<c>this[]</c>), and conventional display (<c>this[int i]</c>).
    /// A name that is not an inherited abstract member still fails as today.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// Throw NotImplementedException in method, property, and indexer stub bodies. Default: true.
    /// When false, methods and getters use a default-return body and setters / init setters use an empty block.
    /// </summary>
    public bool ThrowNotImplemented { get; init; } = true;

    /// <summary>
    /// When true, already-implemented abstract members become eligible
    /// alongside missing members. Existing declarations (override methods,
    /// properties, indexers, and events — including on other partials)
    /// that match a selected member by signature are removed and a standard
    /// generated stub is inserted. Match methods by name + parameter types
    /// in order + <c>RefKind</c>, properties and events by name, and
    /// indexers by the same identity forms as the <c>members</c> filter
    /// (metadata <c>Item</c>, <c>this[]</c>, display forms) plus parameter
    /// types and <c>RefKind</c>. Two same-name existing implementations
    /// with no exact signature match fail with <c>NameCollision</c> — this
    /// flag does not guess. <c>new</c> hiders, explicit interface
    /// implementations, and non-override ordinary members are never
    /// replaced. Generated stubs still honor <see cref="ThrowNotImplemented"/>.
    /// Default: false (only unimplemented abstract members; all-already-
    /// implemented still fails with <c>NoUnimplementedAbstractMembers</c>).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_constructor tool.
/// </summary>
public sealed class GenerateConstructorParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Members"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to add constructor to. Single-site only.
    /// Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick. Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter.
    /// Single-site only.
    /// </summary>
    public int? Column { get; init; }

    /// <summary>
    /// Names of members to initialize. If null or empty, uses instance fields
    /// and, when <see cref="IncludeProperties"/> is true, settable properties.
    /// When <see cref="IncludeInheritedMembers"/> is true, auto-collection and name
    /// resolution also consider accessible inherited members.
    /// When non-empty, listed names are resolved against fields and settable
    /// properties even if <see cref="IncludeProperties"/> is false.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// When true, include settable instance properties as constructor parameters.
    /// When false, collect instance fields only unless <see cref="Members"/> names a property.
    /// Default: true (today's field+settable-property collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>
    /// When true, also collect accessible instance fields (and, when
    /// <see cref="IncludeProperties"/> is true, settable properties)
    /// declared on base types until <c>System.Object</c> / <c>System.ValueType</c>.
    /// Only members visible from this type are included (public / protected /
    /// protected-internal; internal when same assembly). Inherited readonly
    /// fields are skipped because a derived constructor cannot assign them.
    /// Default: false (this-type-only collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// Add null checks for reference type parameters. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool AddNullChecks { get; init; }

    /// <summary>
    /// When true, remove an existing non-implicit constructor with the exact
    /// same signature (same parameter count and types in order) before
    /// generating a fresh constructor — including when that constructor lives
    /// on another partial of the same type. Optional-parameter / required-
    /// parameter ambiguity without an exact match still fails with
    /// <c>ConstructorExists</c>; this flag does not guess which overload to
    /// replace. Default: false (fail if an exact-signature constructor exists).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Accessibility of the generated constructor. Omitted / null / empty
    /// defaults to <c>public</c> (today's behavior). Valid values:
    /// <c>public</c>, <c>private</c>, <c>protected</c>, <c>internal</c>,
    /// <c>protected internal</c>, <c>private protected</c>.
    /// When <see cref="ReplaceExisting"/> replaces an exact-signature
    /// constructor, the new constructor uses this visibility rather than
    /// copying the old constructor's accessibility. Structs and record
    /// structs reject the three protected forms (CS0666).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// When true, generate a single-parameter copy constructor whose
    /// parameter type is the target type and whose body assigns each
    /// selected member from that parameter (<c>this.Name = other.Name;</c>).
    /// When false (default / omitted), keep today's one-parameter-per-member
    /// constructor. Member selection still uses <see cref="Members"/>,
    /// <see cref="IncludeProperties"/>, and <see cref="IncludeInheritedMembers"/>.
    /// <see cref="AddNullChecks"/> null-checks the single copy parameter on
    /// reference types (class / record class) and is skipped on structs /
    /// record structs. Derived records whose base is also a record emit
    /// <c>: base(other)</c>; ordinary classes do not unless
    /// <see cref="ClassBaseCopy"/> is also true. Unsealed records
    /// require <c>public</c> or <c>protected</c> visibility (CS8878).
    /// Copy mode skips properties without an accessible getter.
    /// The copy-constructor signature for
    /// <c>ConstructorExists</c> / <see cref="ReplaceExisting"/> is exactly
    /// one by-value parameter of the target type.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool CopyConstructor { get; init; }

    /// <summary>
    /// When true with <see cref="CopyConstructor"/>, an ordinary class
    /// whose immediate base is a class other than <c>object</c> emits
    /// <c>: base((Base)&lt;copyParameter&gt;)</c> if that base has an accessible
    /// instance constructor with exactly one by-value parameter of the
    /// <em>base</em> type. Inherited assignable members are then not
    /// reassigned in the derived body. Records, record structs, and structs
    /// ignore this flag (records already chain; structs have no class
    /// inheritance). When the base is <c>object</c> / a struct / a record,
    /// or no accessible base copy constructor exists, no class
    /// <c>: base(...)</c> is emitted and generation still succeeds.
    /// When true without <see cref="CopyConstructor"/>, the request is
    /// rejected. Default: false (today's class copy-constructor shape).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ClassBaseCopy { get; init; }

    /// <summary>
    /// When true (and <see cref="CopyConstructor"/> is false), an ordinary
    /// class or record class whose immediate base is a class or (for record
    /// classes) a record other than <c>object</c> emits <c>: base(...)</c>
    /// for an accessible instance constructor whose parameter types
    /// (by-value, same <c>RefKind</c>) are a prefix of the generated
    /// constructor's parameter types. The longest prefix wins; if two
    /// candidates share that length, the one whose parameter names match
    /// the generated names (case-insensitive) wins; a remaining tie is
    /// rejected. Members that produced the passed-through parameters are
    /// not reassigned in the derived body. An accessible parameterless
    /// base constructor emits <c>: base()</c> without skipping body
    /// members. Record structs and structs ignore this flag. When the
    /// base is <c>object</c> / <c>ValueType</c> / a struct, no
    /// <c>: base(...)</c> is emitted and generation still succeeds.
    /// Ordinary-class matching still requires a class (not record) base.
    /// When true with <see cref="CopyConstructor"/>, the request is
    /// rejected. Default: false (today's non-copy shape).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool CallBase { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

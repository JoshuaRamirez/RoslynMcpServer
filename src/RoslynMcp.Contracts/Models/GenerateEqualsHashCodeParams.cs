namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_equals_hashcode tool.
/// </summary>
public sealed class GenerateEqualsHashCodeParams
{
    /// <summary>
    /// Absolute path to the source file containing the type.
    /// Required when <see cref="AllFiles"/> is false.
    /// When <see cref="AllFiles"/> is true, optional and limits the walk
    /// to that one file when set.
    /// </summary>
    public string? SourceFile { get; init; }

    /// <summary>
    /// When true, process every eligible type in every C# document
    /// (or the optional single <see cref="SourceFile"/>).
    /// When true, cannot be combined with <see cref="TypeName"/>,
    /// <see cref="Fields"/>, <see cref="Line"/>, or <see cref="Column"/>.
    /// </summary>
    public bool AllFiles { get; init; }

    /// <summary>
    /// Name of the type to generate Equals/GetHashCode for.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
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
    /// Specific field/property names to include. If null or empty, uses instance fields
    /// and, when <see cref="IncludeProperties"/> is true, readable properties.
    /// When <see cref="IncludeInheritedMembers"/> is true, auto-collection and name
    /// resolution also consider accessible inherited members.
    /// When non-empty, listed names are resolved against fields and properties even if
    /// <see cref="IncludeProperties"/> is false.
    /// Single-site only; cannot be combined with <see cref="AllFiles"/>.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// When true, include readable instance properties as equality members.
    /// When false, collect instance fields only unless <see cref="Fields"/> names a property.
    /// Default: true (today's field+property collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>
    /// When true, also implement <c>IEquatable&lt;T&gt;</c> with a typed <c>Equals(T)</c>
    /// and have <c>Equals(object)</c> delegate to it. Default: false (Equals(object) + GetHashCode only).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ImplementIEquatable { get; init; }

    /// <summary>
    /// When true, also generate <c>operator ==</c> and <c>operator !=</c> that agree with
    /// the generated Equals. Default: false (no equality operators).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool GenerateOperators { get; init; }

    /// <summary>
    /// When true, remove existing Equals/GetHashCode (and IEquatable / operators when those
    /// flags are also set) before generating fresh members. Default: false (fail if they exist).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// When true, emit <c>HashCode.Combine</c> (≤8 members) or a <c>HashCode</c> builder
    /// (&gt;8 members). When false, emit a classic unchecked prime-multiply GetHashCode.
    /// Default: true (today's Combine / builder shape).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool UseHashCodeCombine { get; init; } = true;

    /// <summary>
    /// When true, fold the immediate base type's equality into Equals/GetHashCode
    /// (<c>base.Equals</c> before member comparisons; <c>base.GetHashCode</c> as the
    /// first Combine/Add argument or prime-multiply seed). Rejected when the
    /// immediate base is <c>System.Object</c> or <c>System.ValueType</c>, or when
    /// the immediate base's <c>Equals(object)</c> or <c>GetHashCode</c> is abstract.
    /// Default: false (member-only Equals/GetHashCode).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool CallSuper { get; init; }

    /// <summary>
    /// When true, also collect accessible instance fields (and, when
    /// <see cref="IncludeProperties"/> is true, readable properties) declared on
    /// base types until <c>System.Object</c> / <c>System.ValueType</c>.
    /// Only members visible from this type are included (public / protected /
    /// protected-internal; internal when same assembly). Distinct from
    /// <see cref="CallSuper"/>, which folds <c>base.Equals</c> / <c>base.GetHashCode</c>
    /// and does not change the collected member list.
    /// Default: false (this-type-only collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

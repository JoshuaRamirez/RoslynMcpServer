namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_tostring tool.
/// </summary>
public sealed class GenerateToStringParams
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
    /// Name of the type to generate ToString for.
    /// Single-site only. Required when <see cref="AllFiles"/> is false.
    /// </summary>
    public string? TypeName { get; init; }

    /// <summary>
    /// 1-based line number for disambiguation when several types share
    /// <see cref="TypeName"/>. When set, selects the type whose identifier
    /// or declaration span covers that line (identifier preferred, then
    /// smallest containing type). Omitted keeps today's typeName
    /// <c>FirstOrDefault</c> pick on <c>TypeDeclarationSyntax</c>.
    /// Single-site only.
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// 1-based column for disambiguation. When set with <see cref="Line"/>,
    /// selects the type whose identifier or declaration span covers that
    /// column (identifier preferred, then smallest containing type).
    /// Omitted keeps today's typeName + optional line pick. Column without
    /// line keeps today's first-match after the typeName filter
    /// (<c>TypeDeclarationSyntax</c> only).
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
    /// When true, include readable instance properties as ToString members.
    /// When false, collect instance fields only unless <see cref="Fields"/> names a property.
    /// Default: true (today's field+property collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>
    /// Format: "interpolated" (default) or "stringbuilder". Case-insensitive.
    /// Omitted, null, or empty uses interpolated. Unknown values are rejected.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// When true, also collect accessible instance fields (and, when
    /// <see cref="IncludeProperties"/> is true, readable properties)
    /// declared on base types until <c>System.Object</c> / <c>System.ValueType</c>.
    /// Only members visible from this type are included (public / protected /
    /// protected-internal; internal when same assembly). Distinct from
    /// <see cref="CallSuper"/>, which folds <c>base.ToString()</c> and does
    /// not change the collected member list.
    /// Default: false (this-type-only collection).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// When true, remove an existing parameterless <c>ToString()</c> (instance or
    /// static, including on other partials of the same type) before generating
    /// a fresh instance override. Generic <c>ToString&lt;T&gt;()</c> and
    /// parameterized overloads are left alone.
    /// Default: false (fail if a non-implicit non-generic parameterless ToString exists).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// When true, fold the immediate base type's parameterless instance
    /// <c>ToString()</c> into the generated override
    /// (<c>{base.ToString()}</c> first in an interpolated body;
    /// <c>sb.Append(base.ToString())</c> first on the StringBuilder path).
    /// Rejected when the immediate base is <c>System.Object</c> or
    /// <c>System.ValueType</c>, or when the immediate base's parameterless
    /// instance <c>ToString()</c> is abstract.
    /// Does not change member collection.
    /// Default: false (selected members only; no <c>base.ToString()</c>).
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool CallSuper { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// Valid with <see cref="AllFiles"/>.
    /// </summary>
    public bool Preview { get; init; }
}

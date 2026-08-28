namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_tostring tool.
/// </summary>
public sealed class GenerateToStringParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate ToString for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Specific field/property names to include. If null or empty, uses instance fields
    /// and, when <see cref="IncludeProperties"/> is true, readable properties.
    /// When <see cref="IncludeInheritedMembers"/> is true, auto-collection and name
    /// resolution also consider accessible inherited members.
    /// When non-empty, listed names are resolved against fields and properties even if
    /// <see cref="IncludeProperties"/> is false.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// When true, include readable instance properties as ToString members.
    /// When false, collect instance fields only unless <see cref="Fields"/> names a property.
    /// Default: true (today's field+property collection).
    /// </summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>
    /// Format: "interpolated" (default) or "stringbuilder". Case-insensitive.
    /// Omitted, null, or empty uses interpolated. Unknown values are rejected.
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
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// When true, remove an existing parameterless <c>ToString()</c> (instance or
    /// static, including on other partials of the same type) before generating
    /// a fresh instance override. Generic <c>ToString&lt;T&gt;()</c> and
    /// parameterized overloads are left alone.
    /// Default: false (fail if a non-implicit non-generic parameterless ToString exists).
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
    /// </summary>
    public bool CallSuper { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

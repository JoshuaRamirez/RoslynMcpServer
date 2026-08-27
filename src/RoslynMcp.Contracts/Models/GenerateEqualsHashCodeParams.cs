namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_equals_hashcode tool.
/// </summary>
public sealed class GenerateEqualsHashCodeParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to generate Equals/GetHashCode for.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Specific field/property names to include. If null or empty, uses instance fields
    /// and, when <see cref="IncludeProperties"/> is true, readable properties.
    /// When non-empty, listed names are resolved against fields and properties even if
    /// <see cref="IncludeProperties"/> is false.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// When true, include readable instance properties as equality members.
    /// When false, collect instance fields only unless <see cref="Fields"/> names a property.
    /// Default: true (today's field+property collection).
    /// </summary>
    public bool IncludeProperties { get; init; } = true;

    /// <summary>
    /// When true, also implement <c>IEquatable&lt;T&gt;</c> with a typed <c>Equals(T)</c>
    /// and have <c>Equals(object)</c> delegate to it. Default: false (Equals(object) + GetHashCode only).
    /// </summary>
    public bool ImplementIEquatable { get; init; }

    /// <summary>
    /// When true, also generate <c>operator ==</c> and <c>operator !=</c> that agree with
    /// the generated Equals. Default: false (no equality operators).
    /// </summary>
    public bool GenerateOperators { get; init; }

    /// <summary>
    /// When true, remove existing Equals/GetHashCode (and IEquatable / operators when those
    /// flags are also set) before generating fresh members. Default: false (fail if they exist).
    /// </summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// When true, emit <c>HashCode.Combine</c> (≤8 members) or a <c>HashCode</c> builder
    /// (&gt;8 members). When false, emit a classic unchecked prime-multiply GetHashCode.
    /// Default: true (today's Combine / builder shape).
    /// </summary>
    public bool UseHashCodeCombine { get; init; } = true;

    /// <summary>
    /// When true, fold the immediate base type's equality into Equals/GetHashCode
    /// (<c>base.Equals</c> before member comparisons; <c>base.GetHashCode</c> as the
    /// first Combine/Add argument or prime-multiply seed). Rejected when the
    /// immediate base is <c>System.Object</c> or <c>System.ValueType</c>, or when
    /// the immediate base's <c>Equals(object)</c> or <c>GetHashCode</c> is abstract.
    /// Default: false (member-only Equals/GetHashCode).
    /// </summary>
    public bool CallSuper { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

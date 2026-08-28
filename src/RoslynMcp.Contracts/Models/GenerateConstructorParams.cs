namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Parameters for the generate_constructor tool.
/// </summary>
public sealed class GenerateConstructorParams
{
    /// <summary>
    /// Absolute path to the source file.
    /// </summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// Name of the type to add constructor to.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Names of members to initialize. If null or empty, uses instance fields
    /// and, when <see cref="IncludeProperties"/> is true, settable properties.
    /// When <see cref="IncludeInheritedMembers"/> is true, auto-collection and name
    /// resolution also consider accessible inherited members.
    /// When non-empty, listed names are resolved against fields and settable
    /// properties even if <see cref="IncludeProperties"/> is false.
    /// </summary>
    public IReadOnlyList<string>? Members { get; init; }

    /// <summary>
    /// When true, include settable instance properties as constructor parameters.
    /// When false, collect instance fields only unless <see cref="Members"/> names a property.
    /// Default: true (today's field+settable-property collection).
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
    /// </summary>
    public bool IncludeInheritedMembers { get; init; }

    /// <summary>
    /// Add null checks for reference type parameters. Default: false.
    /// </summary>
    public bool AddNullChecks { get; init; }

    /// <summary>
    /// Return computed changes without applying. Default: false.
    /// </summary>
    public bool Preview { get; init; }
}

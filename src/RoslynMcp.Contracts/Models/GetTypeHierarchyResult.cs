namespace RoslynMcp.Contracts.Models;

/// <summary>
/// Result of a get_type_hierarchy query.
/// </summary>
public sealed class GetTypeHierarchyResult
{
    /// <summary>
    /// The target type name.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Fully qualified name of the target type.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of the type (Class, Interface, Struct, etc.).
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Base types (ancestors), from immediate parent to System.Object.
    /// </summary>
    public required IReadOnlyList<TypeHierarchyEntry> BaseTypes { get; init; }

    /// <summary>
    /// Derived types (descendants).
    /// </summary>
    public required IReadOnlyList<TypeHierarchyEntry> DerivedTypes { get; init; }

    /// <summary>
    /// Implemented interfaces.
    /// </summary>
    public required IReadOnlyList<TypeHierarchyEntry> Interfaces { get; init; }
}

/// <summary>
/// An entry in a type hierarchy.
/// </summary>
public sealed class TypeHierarchyEntry
{
    /// <summary>
    /// Type name.
    /// </summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// Fully qualified name.
    /// </summary>
    public required string FullyQualifiedName { get; init; }

    /// <summary>
    /// Kind of the type.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Source file path (null if from metadata).
    /// </summary>
    public string? File { get; init; }

    /// <summary>
    /// 1-based line number (null if from metadata).
    /// </summary>
    public int? Line { get; init; }
}

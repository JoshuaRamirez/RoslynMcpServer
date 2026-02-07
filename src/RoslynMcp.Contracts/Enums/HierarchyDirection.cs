namespace RoslynMcp.Contracts.Enums;

/// <summary>
/// Direction for type hierarchy traversal.
/// </summary>
public enum HierarchyDirection
{
    /// <summary>Walk up the base type chain.</summary>
    Ancestors,

    /// <summary>Find derived types.</summary>
    Descendants,

    /// <summary>Both ancestors and descendants.</summary>
    Both
}

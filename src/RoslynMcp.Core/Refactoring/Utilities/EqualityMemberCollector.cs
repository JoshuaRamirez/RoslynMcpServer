using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Collects fields and properties suitable for equality/toString generation.
/// </summary>
public static class EqualityMemberCollector
{
    /// <summary>
    /// Gets all fields and auto-properties suitable for equality comparison.
    /// Excludes static, const, and implicitly declared members.
    /// </summary>
    public static List<ISymbol> CollectMembers(INamedTypeSymbol typeSymbol, IReadOnlyList<string>? requestedFields = null)
    {
        var members = new List<ISymbol>();

        // Collect fields (non-static, non-const, non-implicit)
        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                continue;
            members.Add(field);
        }

        // Collect properties (non-static, with getter, non-implicit)
        foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.IsImplicitlyDeclared || prop.GetMethod == null)
                continue;
            // Skip indexers
            if (prop.IsIndexer)
                continue;
            members.Add(prop);
        }

        // Filter to requested members if specified
        if (requestedFields != null && requestedFields.Count > 0)
        {
            var requestedSet = new HashSet<string>(requestedFields);
            members = members.Where(m => requestedSet.Contains(m.Name)).ToList();
        }

        return members;
    }

    /// <summary>
    /// Gets the type of a field or property member.
    /// </summary>
    public static ITypeSymbol GetMemberType(ISymbol member) => member switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        _ => throw new InvalidOperationException($"Unexpected member type: {member.GetType()}")
    };
}

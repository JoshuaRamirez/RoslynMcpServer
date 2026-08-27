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
    /// Forwards to the three-parameter overload with <c>includeProperties: true</c>
    /// so existing NuGet callers keep a stable two-parameter IL signature.
    /// </summary>
    public static List<ISymbol> CollectMembers(INamedTypeSymbol typeSymbol, IReadOnlyList<string>? requestedFields = null)
        => CollectMembers(typeSymbol, requestedFields, includeProperties: true);

    /// <summary>
    /// Gets fields and properties suitable for equality comparison.
    /// Excludes static, const, and implicitly declared members.
    /// When <paramref name="includeProperties"/> is false and no requested names are given,
    /// only instance fields are collected. A non-empty <paramref name="requestedFields"/>
    /// list is authoritative and is resolved against both fields and properties.
    /// </summary>
    public static List<ISymbol> CollectMembers(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<string>? requestedFields,
        bool includeProperties)
    {
        var members = new List<ISymbol>();
        var hasRequestedFields = requestedFields != null && requestedFields.Count > 0;

        // Collect fields (non-static, non-const, non-implicit)
        foreach (var field in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                continue;
            members.Add(field);
        }

        // Collect properties unless includeProperties is false and no explicit names were requested.
        if (includeProperties || hasRequestedFields)
        {
            foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsImplicitlyDeclared || prop.GetMethod == null)
                    continue;
                // Skip indexers
                if (prop.IsIndexer)
                    continue;
                members.Add(prop);
            }
        }

        // Filter to requested members if specified
        if (hasRequestedFields)
        {
            var requestedSet = new HashSet<string>(requestedFields!);
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

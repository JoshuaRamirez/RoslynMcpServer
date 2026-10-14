using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Resolves the declared member, type, or namespace whose accessibility an <c>accessibilityFilter</c>
/// judges for a site. Shared by <c>find_references</c> and <c>get_diagnostics</c> so both walk out
/// through the same symbols that have no declared accessibility of their own (lambdas / anonymous
/// methods, local functions, locals, parameters, type parameters, range variables, and labels).
/// </summary>
internal static class DeclaringSiteSymbol
{
    /// <summary>
    /// The declared member, type, or namespace whose accessibility <c>accessibilityFilter</c> judges for
    /// a site, starting from <paramref name="symbol"/> (the symbol enclosing a usage / diagnostic, or
    /// the declared symbol of a definition location) and walking out through symbols that have no
    /// declared accessibility of their own: lambdas / anonymous methods and local functions (which
    /// report <c>private</c>) and locals, parameters, type parameters, range variables, and labels
    /// (which report <c>NotApplicable</c>). So a reference or diagnostic inside a lambda in a
    /// <c>public</c> method is a <c>public</c> site, and the declaration of a local is judged by the
    /// member declaring it. Namespaces are returned as-is; Roslyn reports them as <c>Public</c>, so
    /// the caller rejects them explicitly (a namespace-level site never matches a filter).
    /// </summary>
    public static ISymbol? Resolve(ISymbol? symbol)
    {
        var current = symbol;
        while (current != null && !IsDeclaringSite(current))
            current = current.ContainingSymbol;
        return current;
    }

    private static bool IsDeclaringSite(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction),
        IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol or INamespaceSymbol => true,
        _ => false
    };
}

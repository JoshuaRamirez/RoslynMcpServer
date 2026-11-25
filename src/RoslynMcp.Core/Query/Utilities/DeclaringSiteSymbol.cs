using Microsoft.CodeAnalysis;

namespace RoslynMcp.Core.Query.Utilities;

/// <summary>
/// Resolves the declared member, type, or namespace whose accessibility an <c>accessibilityFilter</c>
/// judges for a site. Shared by <c>find_references</c> and <c>get_diagnostics</c> so both walk out
/// through the same symbols that have no declared accessibility of their own (lambdas / anonymous
/// methods, local functions, locals, parameters, type parameters, range variables, labels, and
/// synthesized methods on a delegate type) and treat top-level statements as namespace-level sites.
/// Compiler-synthesized property and event backing fields are associated to the property or event
/// the user declared, so <c>kindFilter</c> / <c>accessibilityFilter</c> / <c>nameFilter</c> /
/// <c>staticFilter</c> judge the site as <c>Property</c> or <c>Event</c>, not the private backing
/// field. Ordinary user-declared fields stay <c>Field</c>.
/// </summary>
internal static class DeclaringSiteSymbol
{
    /// <summary>
    /// The declared member, type, or namespace whose accessibility <c>accessibilityFilter</c> judges for
    /// a site, starting from <paramref name="symbol"/> (the symbol enclosing a usage / diagnostic, or
    /// the declared symbol of a definition location) and walking out through symbols that have no
    /// declared accessibility of their own: lambdas / anonymous methods and local functions (which
    /// report <c>private</c>); locals, parameters, type parameters, range variables, and labels
    /// (which report <c>NotApplicable</c>); and synthesized methods on a delegate type (Invoke /
    /// BeginInvoke / EndInvoke / the implicit constructor, which report <c>public</c>). So a
    /// reference or diagnostic inside a lambda in a <c>public</c> method is a <c>public</c> site, a
    /// diagnostic on a <c>private delegate</c> parameter is judged by the delegate (not its public
    /// Invoke), and the declaration of a local is judged by the member declaring it. Namespaces are
    /// returned as-is; Roslyn reports them as <c>Public</c>, so the caller rejects them explicitly
    /// (a namespace-level site never matches a filter). Top-level statements have no declared member
    /// or type: their sites (including those inside top-level local functions, lambdas, and locals)
    /// reach the synthesized <c>&lt;Main&gt;$</c> entry point, which is resolved to its
    /// containing namespace so they are namespace-level sites too (never the synthesized private entry
    /// point or its <c>Program</c> type, even when the user declares <c>public partial class Program</c>).
    /// A compiler-synthesized property or event backing field is associated to that property or event
    /// (including when the walk first reaches the field from a nested lambda), so an auto-property
    /// or field-like event initializer is judged as the declared member, not as <c>Field</c>.
    /// </summary>
    public static ISymbol? Resolve(ISymbol? symbol)
    {
        var current = symbol;
        while (current != null && !IsDeclaringSite(current))
        {
            current = TryAssociateSynthesizedBackingField(current) is { } associated
                ? associated
                : current.ContainingSymbol;
        }
        return current is IMethodSymbol method && IsTopLevelStatementsEntryPoint(method)
            ? method.ContainingNamespace
            : current;
    }

    /// <summary>
    /// When <paramref name="symbol"/> is a compiler-synthesized property or event backing field,
    /// returns the associated property or event so filters and <c>find_callers</c> reporting judge
    /// the site the user declared. Otherwise returns <paramref name="symbol"/>. Ordinary
    /// user-declared fields are unchanged.
    /// </summary>
    public static ISymbol? AssociateSynthesizedBackingField(ISymbol? symbol) =>
        TryAssociateSynthesizedBackingField(symbol) ?? symbol;

    /// <summary>
    /// Whether <paramref name="method"/> is the compiler-synthesized entry point for top-level
    /// statements (<c>&lt;Main&gt;$</c>), which reports a <c>private</c> accessibility no source
    /// declaration gave it. Roslyn does not mark it <see cref="ISymbol.IsImplicitlyDeclared"/> (the
    /// compilation unit declares it), so it is recognized by its unspeakable name, which no source
    /// method can have.
    /// </summary>
    private static bool IsTopLevelStatementsEntryPoint(IMethodSymbol method) =>
        method.Name == WellKnownMemberNames.TopLevelStatementsEntryPointMethodName;

    private static ISymbol? TryAssociateSynthesizedBackingField(ISymbol? symbol) =>
        symbol is IFieldSymbol { IsImplicitlyDeclared: true, AssociatedSymbol: { } associated }
            && associated is IPropertySymbol or IEventSymbol
            ? associated
            : null;

    private static bool IsDeclaringSite(ISymbol symbol) => symbol switch
    {
        // Look through lambdas / local functions and through every method on a delegate type
        // (Invoke / BeginInvoke / EndInvoke / .ctor are synthesized public; the site is the delegate).
        IMethodSymbol method => method.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            && method.ContainingType?.TypeKind is not TypeKind.Delegate,
        // Look through compiler backing fields to the associated property or event.
        IFieldSymbol field when TryAssociateSynthesizedBackingField(field) != null => false,
        IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol or INamespaceSymbol => true,
        _ => false
    };
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared event-field declarator recognition used by generate_overrides,
/// implement_interface, and implement_abstract when collecting existing
/// override / implementation members that are event field declarators
/// (including multi-variable <c>event</c> fields).
/// </summary>
internal static class EventFieldDeclarator
{
    /// <summary>
    /// True when <paramref name="syntax"/> is a
    /// <see cref="VariableDeclaratorSyntax"/> whose grandparent is an
    /// <see cref="EventFieldDeclarationSyntax"/>; sets
    /// <paramref name="eventField"/> and <paramref name="declarator"/>
    /// to that pair (same body as the GenerateOverrides /
    /// ImplementInterface / ImplementAbstract copies).
    /// </summary>
    internal static bool TryGet(
        SyntaxNode syntax,
        out EventFieldDeclarationSyntax eventField,
        out VariableDeclaratorSyntax declarator)
    {
        if (syntax is VariableDeclaratorSyntax variable
            && variable.Parent?.Parent is EventFieldDeclarationSyntax field)
        {
            eventField = field;
            declarator = variable;
            return true;
        }

        eventField = null!;
        declarator = null!;
        return false;
    }
}

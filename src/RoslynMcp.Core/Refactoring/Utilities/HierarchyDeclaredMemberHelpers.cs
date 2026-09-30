using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared declared-member enumeration used by
/// <c>PullMembersUpOperation</c> and <c>PushMembersDownOperation</c>.
/// Same body as the prior private <c>EnumerateDeclaredMembers</c> copies.
/// </summary>
internal static class HierarchyDeclaredMemberHelpers
{
    /// <summary>
    /// Yields each declared method / property / indexer / field variable /
    /// event-field variable / event declaration under
    /// <paramref name="typeDeclaration.Members"/> as
    /// <c>(Name, Symbol, Syntax)</c>. Indexers use the display name
    /// <c>"this[]"</c> (call sites that need the symbol <c>Name</c> remap
    /// today). Same body as the prior private copies.
    /// </summary>
    internal static IEnumerable<(string Name, ISymbol? Symbol, MemberDeclarationSyntax Syntax)> EnumerateDeclaredMembers(
        TypeDeclarationSyntax typeDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    yield return (method.Identifier.Text, semanticModel.GetDeclaredSymbol(method, cancellationToken), method);
                    break;
                case PropertyDeclarationSyntax property:
                    yield return (property.Identifier.Text, semanticModel.GetDeclaredSymbol(property, cancellationToken), property);
                    break;
                case IndexerDeclarationSyntax indexer:
                    yield return ("this[]", semanticModel.GetDeclaredSymbol(indexer, cancellationToken), indexer);
                    break;
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), field);
                    }
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return (variable.Identifier.Text, semanticModel.GetDeclaredSymbol(variable, cancellationToken), eventField);
                    }
                    break;
                case EventDeclarationSyntax eventDecl:
                    yield return (eventDecl.Identifier.Text, semanticModel.GetDeclaredSymbol(eventDecl, cancellationToken), eventDecl);
                    break;
            }
        }
    }
}

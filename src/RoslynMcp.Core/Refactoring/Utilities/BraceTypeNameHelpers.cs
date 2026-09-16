using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared type-name / qualified-path / scope-normalize helpers used by
/// add_braces / remove_braces. Same bodies as the two identical Convert
/// brace copies. Named BraceTypeNameHelpers (not TypeDeclarationHelpers)
/// because <see cref="TypeDeclarationHelpers.FindTypeDeclaration"/> already
/// exists with a different signature (nullable + line/column).
/// </summary>
internal static class BraceTypeNameHelpers
{
    /// <summary>
    /// Normalizes brace scope to statement, file, or type (default statement).
    /// Same body as the two Convert brace copies.
    /// </summary>
    internal static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "statement";

        var normalized = scope.Trim().ToLowerInvariant();
        if (normalized is "statement" or "file" or "type")
            return normalized;

        throw new RefactoringException(
            ErrorCodes.MissingRequiredParam,
            "scope must be statement, file, or type.");
    }

    /// <summary>
    /// Finds the unique <see cref="TypeDeclarationSyntax"/> under
    /// <paramref name="root"/> whose simple or qualified name matches
    /// <paramref name="typeName"/>. Same body as the two Convert brace copies.
    /// </summary>
    /// <exception cref="RefactoringException">
    /// When zero matches (<see cref="ErrorCodes.TypeNotFound"/>) or more than
    /// one (<see cref="ErrorCodes.SymbolAmbiguous"/>).
    /// </exception>
    internal static TypeDeclarationSyntax FindTypeDeclaration(SyntaxNode root, string typeName)
    {
        var matches = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(type => TypeNameMatches(type, typeName))
            .ToList();

        if (matches.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found.");
        }

        if (matches.Count > 1)
        {
            throw new RefactoringException(
                ErrorCodes.SymbolAmbiguous,
                $"Multiple types named '{typeName}' found. Provide a namespace-qualified typeName to disambiguate.");
        }

        return matches[0];
    }

    /// <summary>
    /// True when <paramref name="type"/>'s qualified name, simple identifier,
    /// or qualified suffix equals <paramref name="typeName"/>. Same body as
    /// the two Convert brace copies.
    /// </summary>
    internal static bool TypeNameMatches(TypeDeclarationSyntax type, string typeName)
    {
        var qualified = GetQualifiedTypeName(type);
        if (qualified.Equals(typeName, StringComparison.Ordinal))
            return true;

        if (type.Identifier.Text.Equals(typeName, StringComparison.Ordinal))
            return true;

        return qualified.EndsWith("." + typeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a dot-joined namespace + nested-type path for
    /// <paramref name="type"/>. Same body as the two Convert brace copies.
    /// </summary>
    internal static string GetQualifiedTypeName(TypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        for (var current = (SyntaxNode)type; current != null; current = current.Parent)
        {
            switch (current)
            {
                case TypeDeclarationSyntax declared:
                    parts.Insert(0, declared.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.InsertRange(0, ns.Name.ToString().Split('.'));
                    break;
            }
        }

        return string.Join(".", parts);
    }
}

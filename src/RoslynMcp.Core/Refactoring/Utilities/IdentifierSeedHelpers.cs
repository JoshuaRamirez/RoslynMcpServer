using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared name-seed helpers for the extract operations that derive a suggested
/// identifier from source text (ExtractMethod / ExtractVariable).
/// </summary>
internal static class IdentifierSeedHelpers
{
    /// <summary>
    /// Prefers the invoked simple name of <paramref name="invocation"/>; see
    /// <see cref="PreferInvokedNameFromExpression"/>.
    /// </summary>
    internal static string? PreferInvokedName(InvocationExpressionSyntax invocation) =>
        PreferInvokedNameFromExpression(invocation.Expression);

    /// <summary>
    /// Looks through any parentheses around <paramref name="expression"/> and
    /// returns its simple name: the identifier of an identifier or generic name,
    /// or the right-hand name of a member access. Returns <c>null</c> for any
    /// other expression shape.
    /// </summary>
    internal static string? PreferInvokedNameFromExpression(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            ParenthesizedExpressionSyntax paren => PreferInvokedNameFromExpression(paren.Expression),
            _ => null
        };

    /// <summary>
    /// Prefers the simple name of <paramref name="type"/> (identifier, right side
    /// of a qualified or alias-qualified name, generic identifier, or the element
    /// type of a nullable type); otherwise falls back to
    /// <see cref="SanitizeIdentifierSeed"/> over the type's text. Returns
    /// <c>null</c> for a <c>null</c> type.
    /// </summary>
    internal static string? PreferTypeName(TypeSyntax? type, bool capitalizeFirstWord) =>
        type switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            NullableTypeSyntax n => PreferTypeName(n.ElementType, capitalizeFirstWord),
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            _ => type == null ? null : SanitizeIdentifierSeed(type.ToString(), capitalizeFirstWord)
        };

    /// <summary>
    /// Keeps only letters and digits from <paramref name="text"/>, upper-casing the
    /// first letter of every word after the first. The first word's leading letter
    /// is upper-cased when <paramref name="capitalizeFirstWord"/> is <c>true</c>
    /// (PascalCase, method names) and lower-cased otherwise (camelCase, variable
    /// names). Returns <c>null</c> for blank input or when nothing survives.
    /// </summary>
    internal static string? SanitizeIdentifierSeed(string text, bool capitalizeFirstWord)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new StringBuilder(text.Length);
        var startNewWord = true;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (startNewWord && char.IsLetter(c))
                {
                    builder.Append(builder.Length == 0 && !capitalizeFirstWord
                        ? char.ToLowerInvariant(c)
                        : char.ToUpperInvariant(c));
                    startNewWord = false;
                }
                else
                {
                    builder.Append(c);
                    startNewWord = false;
                }
            }
            else
            {
                startNewWord = true;
            }
        }

        var name = builder.ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

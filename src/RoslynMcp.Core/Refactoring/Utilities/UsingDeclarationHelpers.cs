using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>using</c> / <c>await using</c> local-declaration predicate used by
/// <c>IntroduceFieldOperation</c>, <c>IntroduceParameterOperation</c>, and
/// <c>InlineVariableOperation</c>. Same body as the prior private copies.
/// </summary>
internal static class UsingDeclarationHelpers
{
    /// <summary>
    /// True for <c>using var</c> / <c>await using</c> locals (and classic
    /// <c>using (...)</c> declarators). Same body as the prior private
    /// <c>IsUsingDeclaration</c> copies on IntroduceField / IntroduceParameter /
    /// InlineVariable.
    /// </summary>
    internal static bool IsUsingDeclaration(VariableDeclaratorSyntax declarator) =>
        declarator.Parent?.Parent switch
        {
            LocalDeclarationStatementSyntax statement => statement.UsingKeyword != default,
            UsingStatementSyntax => true,
            _ => false
        };
}

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>throw new global::System.NotImplementedException();</c> block
/// used by implement_abstract and generate_method_stub placeholder bodies.
/// </summary>
internal static class ThrowNotImplementedBodyHelpers
{
    /// <summary>
    /// Builds a single-statement block that throws
    /// <c>global::System.NotImplementedException</c> with an empty argument
    /// list. Same body as the ImplementAbstract /
    /// GenerateMethodStub copies (not the unqualified
    /// <see cref="SyntaxGenerationHelper"/> private variant).
    /// </summary>
    internal static BlockSyntax CreateThrowNotImplementedBody()
    {
        return SyntaxFactory.Block(
            SyntaxFactory.ThrowStatement(
                SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("global::System.NotImplementedException"))
                .WithArgumentList(SyntaxFactory.ArgumentList())));
    }
}

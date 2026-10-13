using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcp.Core.Refactoring.Utilities;

/// <summary>
/// Shared <c>throw new global::System.NotImplementedException();</c> block
/// and CS8156 ref-return throw gate used by implement_abstract and
/// generate_method_stub / SyntaxGenerationHelper placeholder bodies.
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

    /// <summary>
    /// True when a method body must throw: the flag is on, or the method
    /// returns <c>ref</c> / <c>ref readonly</c> (CS8156). Same body as the
    /// former private copy on <c>ImplementAbstractOperation</c>.
    /// </summary>
    internal static bool RequiresThrowBody(IMethodSymbol method, bool throwNotImplemented) =>
        throwNotImplemented || method.ReturnsByRef || method.ReturnsByRefReadonly;

    /// <summary>
    /// True when a property / indexer getter must throw: the flag is on, or
    /// the property returns <c>ref</c> / <c>ref readonly</c> (CS8156). Same
    /// body as the former private copy on <see cref="SyntaxGenerationHelper"/>.
    /// </summary>
    internal static bool RequiresThrowBody(IPropertySymbol property, bool throwNotImplemented) =>
        throwNotImplemented || property.ReturnsByRef || property.ReturnsByRefReadonly;
}

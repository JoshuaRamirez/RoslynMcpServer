using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="TypeInsertionHelpers"/> —
/// InsertTypeDeclaration into namespace vs compilation unit,
/// FindNamespace match/miss, and GetTypeInsertionPosition
/// enclosing-namespace vs root fallback.
/// </summary>
public class TypeInsertionHelpersTests
{
    [Fact]
    public void InsertTypeDeclaration_IntoNamespace_AddsMember()
    {
        var root = Parse("""
            namespace N
            {
                class Existing { }
            }
            """);
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Single();
        var newType = (TypeDeclarationSyntax)SyntaxFactory.ParseCompilationUnit("class NewType { }").Members[0];

        var updated = TypeInsertionHelpers.InsertTypeDeclaration(root, ns, newType);
        var names = updated.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .ToList();

        Assert.Contains("Existing", names);
        Assert.Contains("NewType", names);
        Assert.Contains(
            updated.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Single().Members,
            m => m is TypeDeclarationSyntax td && td.Identifier.Text == "NewType");
    }

    [Fact]
    public void InsertTypeDeclaration_IntoCompilationUnit_AddsMember()
    {
        var root = Parse("""
            class Existing { }
            """);
        var newType = (TypeDeclarationSyntax)SyntaxFactory.ParseCompilationUnit("class NewType { }").Members[0];

        var updated = TypeInsertionHelpers.InsertTypeDeclaration(root, root, newType);
        var names = ((CompilationUnitSyntax)updated).Members
            .OfType<TypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .ToList();

        Assert.Equal(new[] { "Existing", "NewType" }, names);
    }

    [Fact]
    public void FindNamespace_Match_ReturnsLastMatchingNamespace()
    {
        var root = Parse("""
            namespace Outer
            {
                namespace Inner
                {
                    class C { }
                }
            }
            """);

        var found = TypeInsertionHelpers.FindNamespace(root, "Outer.Inner");
        Assert.NotNull(found);
        Assert.Equal("Inner", found!.Name.ToString());
    }

    [Fact]
    public void FindNamespace_Miss_ReturnsNull()
    {
        var root = Parse("""
            namespace Outer
            {
                class C { }
            }
            """);

        Assert.Null(TypeInsertionHelpers.FindNamespace(root, "Missing"));
        Assert.Null(TypeInsertionHelpers.FindNamespace(root, null));
        Assert.Null(TypeInsertionHelpers.FindNamespace(root, ""));
    }

    [Fact]
    public void GetTypeInsertionPosition_BlockNamespace_ReturnsCloseBraceStart()
    {
        var root = Parse("""
            namespace N
            {
                class C
                {
                    void M() { var x = new { A = 1 }; }
                }
            }
            """);
        var creation = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().Single();
        var ns = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().Single();

        var position = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        Assert.Equal(ns.CloseBraceToken.SpanStart, position);
    }

    [Fact]
    public void GetTypeInsertionPosition_NoNamespace_FallsBackToRootEnd()
    {
        var root = Parse("""
            class C
            {
                void M() { var x = new { A = 1 }; }
            }
            """);
        var creation = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().Single();

        var position = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        Assert.Equal(root.Span.End, position);
    }

    [Fact]
    public void GetTypeInsertionPosition_FileScopedNamespace_ReturnsNamespaceEnd()
    {
        var root = Parse("""
            namespace N;

            class C
            {
                void M() { var x = new { A = 1 }; }
            }
            """);
        var creation = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().Single();
        var ns = root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Single();

        var position = TypeInsertionHelpers.GetTypeInsertionPosition(root, creation);
        Assert.Equal(ns.Span.End, position);
    }

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot();
}

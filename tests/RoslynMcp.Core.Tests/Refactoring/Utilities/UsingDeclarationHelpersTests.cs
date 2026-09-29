using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

public class UsingDeclarationHelpersTests
{
    [Fact]
    public void IsUsingDeclaration_True_ForUsingVarLocal()
    {
        var declarator = FirstDeclarator("""
            class C
            {
                void M()
                {
                    using var stream = new System.IO.MemoryStream();
                }
            }
            """);

        Assert.True(UsingDeclarationHelpers.IsUsingDeclaration(declarator));
    }

    [Fact]
    public void IsUsingDeclaration_True_ForAwaitUsingVarLocal()
    {
        var declarator = FirstDeclarator("""
            class C
            {
                async System.Threading.Tasks.Task M()
                {
                    await using var stream = new System.IO.MemoryStream();
                }
            }
            """);

        Assert.True(UsingDeclarationHelpers.IsUsingDeclaration(declarator));
    }

    [Fact]
    public void IsUsingDeclaration_True_ForClassicUsingStatementDeclarator()
    {
        var declarator = FirstDeclarator("""
            class C
            {
                void M()
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                    }
                }
            }
            """);

        Assert.True(UsingDeclarationHelpers.IsUsingDeclaration(declarator));
    }

    [Fact]
    public void IsUsingDeclaration_False_ForOrdinaryLocal()
    {
        var declarator = FirstDeclarator("""
            class C
            {
                void M()
                {
                    var value = 1;
                }
            }
            """);

        Assert.False(UsingDeclarationHelpers.IsUsingDeclaration(declarator));
    }

    [Fact]
    public void IsUsingDeclaration_False_ForFieldDeclarator()
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText("""
            class C
            {
                int field = 1;
            }
            """).GetRoot();
        var declarator = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();

        Assert.False(UsingDeclarationHelpers.IsUsingDeclaration(declarator));
    }

    private static VariableDeclaratorSyntax FirstDeclarator(string source)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();
    }
}

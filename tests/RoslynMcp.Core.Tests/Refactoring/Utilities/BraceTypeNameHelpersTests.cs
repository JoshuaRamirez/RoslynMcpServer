using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="BraceTypeNameHelpers"/> —
/// FindTypeDeclaration hit/miss, GetQualifiedTypeName nested,
/// NormalizeScope null/whitespace/value, TypeNameMatches true/false.
/// </summary>
public class BraceTypeNameHelpersTests
{
    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(source).GetRoot();

    [Fact]
    public void FindTypeDeclaration_Hit_ReturnsType()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);

        var type = BraceTypeNameHelpers.FindTypeDeclaration(root, "Worker");
        Assert.Equal("Worker", type.Identifier.Text);
        Assert.Equal("Sample.Worker", BraceTypeNameHelpers.GetQualifiedTypeName(type));
    }

    [Fact]
    public void FindTypeDeclaration_Miss_ThrowsTypeNotFound()
    {
        var root = Parse("""
            class Worker { }
            """);

        var ex = Assert.Throws<RefactoringException>(() =>
            BraceTypeNameHelpers.FindTypeDeclaration(root, "Missing"));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [Fact]
    public void GetQualifiedTypeName_NestedTypes_JoinsPath()
    {
        var root = Parse("""
            namespace Outer.Inner
            {
                class Host
                {
                    class Nested { }
                }
            }
            """);
        var nested = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Single(t => t.Identifier.Text == "Nested");

        Assert.Equal("Outer.Inner.Host.Nested", BraceTypeNameHelpers.GetQualifiedTypeName(nested));
    }

    [Fact]
    public void NormalizeScope_NullOrWhitespace_DefaultsToStatement()
    {
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope(null));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope(""));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope("   "));
    }

    [Fact]
    public void NormalizeScope_Value_TrimsAndLowercases()
    {
        Assert.Equal("file", BraceTypeNameHelpers.NormalizeScope("FILE"));
        Assert.Equal("type", BraceTypeNameHelpers.NormalizeScope(" Type "));
        Assert.Equal("statement", BraceTypeNameHelpers.NormalizeScope("Statement"));
    }

    [Fact]
    public void TypeNameMatches_True_ForSimpleAndQualified()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Single();

        Assert.True(BraceTypeNameHelpers.TypeNameMatches(type, "Worker"));
        Assert.True(BraceTypeNameHelpers.TypeNameMatches(type, "Sample.Worker"));
    }

    [Fact]
    public void TypeNameMatches_False_ForDifferentName()
    {
        var root = Parse("""
            namespace Sample
            {
                class Worker { }
            }
            """);
        var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Single();

        Assert.False(BraceTypeNameHelpers.TypeNameMatches(type, "Other"));
        Assert.False(BraceTypeNameHelpers.TypeNameMatches(type, "Sample.Other"));
    }
}

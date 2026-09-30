using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="HierarchyTypeDeclarationHelpers"/> —
/// FindTypeDeclaration previously duplicated on PullMembersUp /
/// PushMembersDown (FQN simpleName strip; enum/delegate in the line set).
/// </summary>
public class HierarchyTypeDeclarationHelpersTests
{
    private const string EnumFirstThenSameNamedClassSource = """
        namespace TestApp
        {
            public enum Animal // animal-enum
            {
                None
            }

            public class Animal // animal-class
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace TestApp
        {
            public delegate void Animal(); // animal-delegate

            public class Animal // animal-class
            {
                public string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(
            root, "Animal", FindLine(EnumFirstThenSameNamedClassSource, "animal-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(
            root, "Animal", FindLine(EnumFirstThenSameNamedClassSource, "animal-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(root, "Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(
            root, "Animal", FindLine(DelegateFirstThenSameNamedClassSource, "animal-delegate"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_FqnTypeName_UsesSimpleName()
    {
        const string source = """
            namespace TestApp
            {
                public class Animal
                {
                    public string Name { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(
            root, "TestApp.Animal", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
        Assert.Equal("Animal", ((ClassDeclarationSyntax)found).Identifier.Text);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        const string source = """
            namespace TestApp
            {
                public class Animal
                {
                    public class Nested // nested-id
                    {
                    }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = HierarchyTypeDeclarationHelpers.FindTypeDeclaration(
            root, "Nested", FindLine(source, "nested-id"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
        Assert.Equal("Nested", ((ClassDeclarationSyntax)found).Identifier.Text);
    }

    private static int FindLine(string source, string marker)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        }

        throw new InvalidOperationException($"Marker '{marker}' not found.");
    }
}

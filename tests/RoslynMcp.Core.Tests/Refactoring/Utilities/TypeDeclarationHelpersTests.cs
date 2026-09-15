using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="TypeDeclarationHelpers"/> —
/// CollectTypeDeclarations ordering/inclusion, AddMembers trivia shape,
/// and FindTypeDeclaration line/column selection.
/// </summary>
public class TypeDeclarationHelpersTests
{
    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public abstract class Eligible
        {
            public abstract void Go();
        }

        public static class StaticSkip
        {
        }

        public interface ISkip
        {
            void Go();
        }

        public struct PointSkip
        {
        }

        public class Outer
        {
            public class Nested
            {
            }
        }
        """;

    [Fact]
    public void CollectTypeDeclarations_IncludesNestedAndInterface_OrderedBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(MixedEligibleAndSkipped).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        var names = types.Select(t => t.Identifier.Text).ToList();

        Assert.Contains("Eligible", names);
        Assert.Contains("StaticSkip", names);
        Assert.Contains("ISkip", names);
        Assert.Contains("PointSkip", names);
        Assert.Contains("Outer", names);
        Assert.Contains("Nested", names);
        Assert.True(names.IndexOf("Outer") < names.IndexOf("Nested"));

        // SpanStart primary order matches source declaration order for top-level types.
        Assert.Equal(
            new[] { "Eligible", "StaticSkip", "ISkip", "PointSkip", "Outer", "Nested" },
            names);
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesClassStructInterfaceRecord_ExcludesEnumAndDelegate()
    {
        const string source = """
            namespace TestApp;
            public class C { }
            public struct S { }
            public interface I { }
            public enum E { A }
            public delegate void D();
            public record R();
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        Assert.Equal(4, types.Count);
        Assert.Contains(types, t => t.Identifier.Text == "C");
        Assert.Contains(types, t => t.Identifier.Text == "S");
        Assert.Contains(types, t => t.Identifier.Text == "I");
        Assert.Contains(types, t => t.Identifier.Text == "R");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "E");
        Assert.DoesNotContain(types, t => t.Identifier.Text == "D");
    }

    [Fact]
    public void CollectTypeDeclarations_OrdersBySpanStart_PreservesSourceOrder()
    {
        // Distinct type decls in one tree always have different SpanStart values, so
        // this asserts the primary OrderBy(SpanStart) key — not the ThenBy(Length)
        // defensive tie-break (same-start ties do not occur for real syntax nodes).
        const string source = """
            class A { }
            class B { }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var types = TypeDeclarationHelpers.CollectTypeDeclarations(root);
        Assert.Equal(new[] { "A", "B" }, types.Select(t => t.Identifier.Text));
        Assert.True(types[0].SpanStart < types[1].SpanStart);
    }

    [Fact]
    public void AddMembers_AppendsWithBlankLineLeadingAndTrailingCrlf()
    {
        var type = (TypeDeclarationSyntax)SyntaxFactory.ParseCompilationUnit(
            """
            class C
            {
                void Existing() { }
            }
            """).Members[0];

        var newMember = SyntaxFactory.ParseMemberDeclaration("void Added() { }")!;
        var updated = TypeDeclarationHelpers.AddMembers(type, [newMember]);

        Assert.Equal(2, updated.Members.Count);
        Assert.Equal("Existing", ((MethodDeclarationSyntax)updated.Members[0]).Identifier.Text);
        Assert.Equal("Added", ((MethodDeclarationSyntax)updated.Members[1]).Identifier.Text);

        var expectedNewline = SyntaxFactory.CarriageReturnLineFeed.ToFullString();
        var leading = updated.Members[1].GetLeadingTrivia().ToList();
        Assert.Equal(2, leading.Count);
        Assert.All(leading, t =>
        {
            Assert.True(t.IsKind(SyntaxKind.EndOfLineTrivia));
            Assert.Equal(expectedNewline, t.ToFullString());
        });

        var trailing = updated.Members[1].GetTrailingTrivia().ToList();
        Assert.Single(trailing);
        Assert.True(trailing[0].IsKind(SyntaxKind.EndOfLineTrivia));
        Assert.Equal(expectedNewline, trailing[0].ToFullString());
    }

    [Fact]
    public void AddMembers_EmptyList_PreservesMemberCount()
    {
        var type = (TypeDeclarationSyntax)SyntaxFactory.ParseCompilationUnit(
            """
            class C
            {
                void Existing() { }
            }
            """).Members[0];

        var updated = TypeDeclarationHelpers.AddMembers(type, []);

        var only = Assert.Single(updated.Members);
        Assert.Equal("Existing", ((MethodDeclarationSyntax)only).Identifier.Text);
    }

    private const string NestedSameNamePersonSource = """
        namespace TestApp;

        public class Person // outer-person
        {
            public string Name { get; set; }

            public class Person // nested-person
            {
                public int Age { get; set; }
            }
        }
        """;

    private const string SameLineNestedPersonSource = """
        namespace TestApp;

        public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = TypeDeclarationHelpers.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = TypeDeclarationHelpers.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "nested-person"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = TypeDeclarationHelpers.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "outer-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = TypeDeclarationHelpers.FindTypeDeclaration(root, "Person", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = TypeDeclarationHelpers.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = TypeDeclarationHelpers.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");
        var found = TypeDeclarationHelpers.FindTypeDeclaration(
            root, "Person", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = TypeDeclarationHelpers.FindTypeDeclaration(root, "Person", line: 1, column: 1);

        Assert.Null(found);
    }

    [Fact]
    public void FindTypeDeclaration_UnknownName_ReturnsNull()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        Assert.Null(TypeDeclarationHelpers.FindTypeDeclaration(root, "Missing", line: null));
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }

                public class Person // nested-person
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var found = TypeDeclarationHelpers.FindTypeDeclaration(root, "Person", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="FindMethodHelpers.FindMethod"/>,
/// <see cref="FindMethodHelpers.FindMethodDeclaration"/>, and
/// <see cref="FindMethodHelpers.CollectMethods"/> / <see cref="FindMethodHelpers.OrderMethods"/> —
/// selection / throwing / allFiles enumeration previously covered on Signature private copies.
/// </summary>
public class FindMethodHelpersTests
{
    private const string SameLineOverloadsSource = """
        class C
        {
            public void Process(int x) { } public void Process(int x, int y) { }
        }
        """;

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineOverloadsSource).GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var first = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { }"));
        var second = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y)"));
        var omitted = FindMethodHelpers.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Single(first.ParameterList.Parameters);
        Assert.Equal(2, second.ParameterList.Parameters.Count);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(int x) { }

                public void Process(int x, int y) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(int x) { }");
        Assert.NotEqual(startLine, identifierLine);

        var byStartLineOnly = FindMethodHelpers.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = FindMethodHelpers.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Single(byColumn.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(int x)");

        var atSecondStart = FindMethodHelpers.FindMethod(root, "Process", line, secondStart);
        var atSecondId = FindMethodHelpers.FindMethod(root, "Process", line, secondId);
        var atFirstId = FindMethodHelpers.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = FindMethodHelpers.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", atSecondStart.Identifier.Text);
        Assert.Equal("Process", atSecondId.Identifier.Text);
        Assert.Equal("Other", atFirstId.Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void FindMethod_SingleMatch_OmittedLine_ReturnsMethod()
    {
        const string source = """
            class C
            {
                public void Only(int x) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = FindMethodHelpers.FindMethod(root, "Only", line: null, column: null);
        Assert.NotNull(found);
        Assert.Equal("Only", found.Identifier.Text);
    }

    [Fact]
    public void FindMethod_NoMatch_ReturnsNull()
    {
        const string source = """
            class C
            {
                public void Only(int x) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        Assert.Null(FindMethodHelpers.FindMethod(root, "Missing", line: null, column: null));
    }

    [Fact]
    public void StartLine_UsesDeclarationLocation()
    {
        const string source = """
            class C
            {
                public void
                Process(int x) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        Assert.Equal(FindLine(source, "public void"), FindMethodHelpers.StartLine(method));
        Assert.NotEqual(FindLine(source, "Process(int x)"), FindMethodHelpers.StartLine(method));
    }


    [Fact]
    public void FindMethodDeclaration_NoMatch_ThrowsMethodNotFound()
    {
        const string source = """
            class C
            {
                public void Only(int x) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var ex = Assert.Throws<RefactoringException>(() =>
            FindMethodHelpers.FindMethodDeclaration(root, "Missing", line: null, column: null));
        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void FindMethodDeclaration_MultipleWithoutLine_ThrowsSymbolAmbiguous()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineOverloadsSource).GetRoot();
        var ex = Assert.Throws<RefactoringException>(() =>
            FindMethodHelpers.FindMethodDeclaration(root, "Process", line: null, column: null));
        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
    }

    [Fact]
    public void FindMethodDeclaration_ColumnPath_PicksCoveringMethod()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineOverloadsSource).GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var found = FindMethodHelpers.FindMethodDeclaration(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y)"));
        Assert.Equal(2, found.ParameterList.Parameters.Count);
    }

    [Fact]
    public void FindMethodDeclaration_SingleMatch_ReturnsMethod()
    {
        const string source = """
            class C
            {
                public void Only(int x) { }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var found = FindMethodHelpers.FindMethodDeclaration(root, "Only", line: null, column: null);
        Assert.Equal("Only", found.Identifier.Text);
    }

    [Fact]
    public void CollectMethods_EmptyCompilationUnit_ReturnsEmpty()
    {
        var root = CSharpSyntaxTree.ParseText("").GetRoot();
        Assert.Empty(FindMethodHelpers.CollectMethods(root));
    }

    [Fact]
    public void CollectMethods_SingleMethod_ReturnsThatMethod()
    {
        const string source = """
            class C
            {
                public void Only(int x) { }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = FindMethodHelpers.CollectMethods(root);
        Assert.Single(methods);
        Assert.Equal("Only", methods[0].Identifier.Text);
    }

    [Fact]
    public void CollectMethods_MatchesOrderMethodsOnDescendants()
    {
        const string source = """
            class C
            {
                public void B() { }
                public void A(int x) { } public void A() { }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var collected = FindMethodHelpers.CollectMethods(root);
        var ordered = FindMethodHelpers.OrderMethods(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>());
        Assert.Equal(
            ordered.Select(m => (m.SpanStart, m.Span.Length, m.Identifier.Text)),
            collected.Select(m => (m.SpanStart, m.Span.Length, m.Identifier.Text)));
        Assert.Equal(["B", "A", "A"], collected.Select(m => m.Identifier.Text).ToList());
    }

    [Fact]
    public void OrderMethods_EqualSpanStart_OrdersByLength()
    {
        // Independently parsed members share SpanStart 0, so ThenBy Length decides.
        var longer = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            "void A(int x, int y) { }")!;
        var shorter = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(
            "void A() { }")!;
        Assert.Equal(longer.SpanStart, shorter.SpanStart);
        Assert.True(shorter.Span.Length < longer.Span.Length);

        var ordered = FindMethodHelpers.OrderMethods([longer, shorter]);
        Assert.Equal(2, ordered.Count);
        Assert.Same(shorter, ordered[0]);
        Assert.Same(longer, ordered[1]);

        var already = FindMethodHelpers.OrderMethods([shorter, longer]);
        Assert.Same(shorter, already[0]);
        Assert.Same(longer, already[1]);
    }

    [Fact]
    public void OrderMethods_DistinctSpanStart_IgnoresInputOrder()
    {
        const string source = """
            class C
            {
                public void B(int first, int second, int third) { }
                public void A() { }
            }
            """;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var natural = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        Assert.Equal(2, natural.Count);
        Assert.True(natural[0].SpanStart < natural[1].SpanStart);
        Assert.True(natural[0].Span.Length > natural[1].Span.Length);

        var ordered = FindMethodHelpers.OrderMethods(natural.AsEnumerable().Reverse());
        Assert.Equal(
            natural.Select(m => m.Identifier.Text),
            ordered.Select(m => m.Identifier.Text));
        Assert.True(ordered[0].SpanStart < ordered[1].SpanStart);
    }

    private static int FindLine(string source, string fragment)
    {
        var idx = source.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Fragment not found: {fragment}");
        return source[..idx].Count(c => c == '\n') + 1;
    }

    private static int ColumnOf(string source, string fragment)
    {
        var idx = source.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"Fragment not found: {fragment}");
        var lineStart = source.LastIndexOf('\n', idx) + 1;
        return idx - lineStart + 1;
    }
}

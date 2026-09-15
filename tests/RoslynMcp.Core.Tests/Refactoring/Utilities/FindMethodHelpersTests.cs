using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Refactoring.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Utilities;

/// <summary>
/// Unit tests for <see cref="FindMethodHelpers.FindMethod"/> —
/// selection behavior previously covered on Signature private copies.
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

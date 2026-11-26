using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Query.Utilities;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3056: get_code_metrics skipped every line whose text starts with "//",
/// including content lines of raw, verbatim and interpolated string literals, so linesOfCode was
/// undercounted (and the maintainability index inflated).
/// </summary>
public class MetricsCalculatorStringLiteralCommentLinesTests
{
    private const string Source = """"
        namespace App;

        public class M
        {
            public string Plain()
            {
                var a = "line1";
                var b = "line2";
                var c = "line3";
                return a + b + c;
            }

            public string Raw()
            {
                return """
                    // Generated header
                    // do not edit
                    // https://example.com
                    body
                    """;
            }

            public string Verbatim()
            {
                return @"
        // Generated header
        // do not edit
        // https://example.com
        body
        ";
            }

            public string InterpolatedRaw(int n)
            {
                return $"""
                    // Generated header {n}
                    // do not edit
                    // https://example.com
                    body
                    """;
            }

            public string Commented()
            {
                // a real comment
                /// not a doc comment position, still trivia
                var x = "// not a comment"; // trailing
                /* block
                // inside block */
                return x;
            }
        }
        """";

    [Theory]
    [InlineData("Plain", 7)]
    [InlineData("Raw", 9)]
    [InlineData("Verbatim", 9)]
    [InlineData("InterpolatedRaw", 9)]
    [InlineData("Commented", 6)]
    public void LinesOfCode_CountsStringLiteralLinesStartingWithSlashes(string method, int expected)
    {
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        var node = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == method);

        Assert.Equal(expected, MetricsCalculator.CalculateLinesOfCode(node));
    }

    [Fact]
    public void MaintainabilityIndex_OfLongerRawMethod_IsNotHigherThanPlain()
    {
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        int Mi(string name) => MetricsCalculator.CalculateMaintainabilityIndex(
            1,
            MetricsCalculator.CalculateLinesOfCode(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == name)));

        Assert.True(Mi("Raw") < Mi("Plain"));
    }
}

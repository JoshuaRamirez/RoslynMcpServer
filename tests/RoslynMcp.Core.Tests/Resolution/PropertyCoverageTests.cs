using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

public class PropertyCoverageTests
{
    [Fact]
    public void PropertyCoversColumn_True_OnIdentifier_False_AtExclusiveEnd()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                int P { get; set; }
            }
            """);
        var root = tree.GetRoot();
        var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Single();
        var idSpan = property.Identifier.GetLocation().GetLineSpan();
        var line = idSpan.StartLinePosition.Line + 1;
        var startCol = idSpan.StartLinePosition.Character + 1;
        var endCol = idSpan.EndLinePosition.Character + 1;

        Assert.True(PropertyCoverage.PropertyCoversColumn(property, line, startCol));
        Assert.True(PropertyCoverage.IdentifierCoversColumn(property, line, startCol));
        Assert.True(PropertyCoverage.IdentifierCoversColumn(property, line, endCol - 1));
        Assert.False(PropertyCoverage.IdentifierCoversColumn(property, line, endCol));
        // endCol is past the identifier exclusive end but still inside the property span.
        Assert.True(PropertyCoverage.PropertyCoversColumn(property, line, endCol));
        Assert.False(PropertyCoverage.IdentifierCoversColumn(property, line, startCol - 1));
    }
}

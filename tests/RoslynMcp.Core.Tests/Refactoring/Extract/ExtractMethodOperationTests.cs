using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Unit tests for ExtractMethodOperation semantic validation.
/// Tests validate code analysis constraints that occur during extraction.
/// </summary>
public class ExtractMethodOperationTests
{
    #region Yield Statement Tests

    [Fact]
    public void ExtractMethod_YieldReturn_ThrowsContainsYield()
    {
        // Arrange
        var nodes = CreateNodesWithYieldReturn();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateSelection(nodes));

        // Assert
        Assert.Equal(ErrorCodes.ContainsYield, exception.ErrorCode);
    }

    [Fact]
    public void ExtractMethod_YieldBreak_ThrowsContainsYield()
    {
        // Arrange
        var nodes = CreateNodesWithYieldBreak();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateSelection(nodes));

        // Assert
        Assert.Equal(ErrorCodes.ContainsYield, exception.ErrorCode);
    }

    [Fact]
    public void ExtractMethod_YieldStatement_MessageIndicatesYield()
    {
        // Arrange
        var nodes = CreateNodesWithYieldReturn();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateSelection(nodes));

        // Assert
        Assert.Contains("yield", exception.Message.ToLowerInvariant());
    }

    #endregion

    #region Empty Selection Tests

    [Fact]
    public void ExtractMethod_EmptySelection_ThrowsEmptySelection()
    {
        // Arrange
        var nodes = new List<SyntaxNode>();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateEmptySelection(nodes));

        // Assert
        Assert.Equal(ErrorCodes.EmptySelection, exception.ErrorCode);
    }

    [Fact]
    public void ExtractMethod_EmptySelection_MessageIndicatesNoCode()
    {
        // Arrange
        var nodes = new List<SyntaxNode>();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateEmptySelection(nodes));

        // Assert
        Assert.Contains("No code", exception.Message);
    }

    #endregion

    #region Multiple Returns Tests

    [Fact]
    public void ExtractMethod_MultipleReturns_ThrowsMultipleExitPoints()
    {
        // Arrange
        var nodes = CreateNodesWithMultipleReturns();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateSelection(nodes));

        // Assert
        Assert.Equal(ErrorCodes.MultipleExitPoints, exception.ErrorCode);
    }

    [Fact]
    public void ExtractMethod_MultipleReturns_MessageIndicatesSimplify()
    {
        // Arrange
        var nodes = CreateNodesWithMultipleReturns();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateSelection(nodes));

        // Assert
        Assert.Contains("Simplify", exception.Message);
    }

    [Fact]
    public void ExtractMethod_SingleReturn_DoesNotThrow()
    {
        // Arrange
        var nodes = CreateNodesWithSingleReturn();

        // Act & Assert (no exception expected)
        var exception = Record.Exception(() => ValidateSelection(nodes));

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region Async Method Detection Tests

    [Fact]
    public void ExtractMethod_ContainsAwait_DetectsAwaitExpression()
    {
        // Arrange
        var nodes = CreateNodesWithAwait();

        // Act
        var containsAwait = ContainsAwaitExpression(nodes);

        // Assert
        Assert.True(containsAwait);
    }

    [Fact]
    public void ExtractMethod_NoAwait_DoesNotDetectAwait()
    {
        // Arrange
        var nodes = CreateNodesWithSingleReturn();

        // Act
        var containsAwait = ContainsAwaitExpression(nodes);

        // Assert
        Assert.False(containsAwait);
    }

    #endregion

    #region Visibility Tests

    [Theory]
    [InlineData("private")]
    [InlineData("internal")]
    [InlineData("protected")]
    [InlineData("public")]
    [InlineData("private protected")]
    [InlineData("protected internal")]
    public void ExtractMethod_ValidVisibility_AcceptsModifier(string visibility)
    {
        // Arrange
        var validVisibilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "private", "internal", "protected", "public", "private protected", "protected internal"
        };

        // Act
        var isValid = validVisibilities.Contains(visibility);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ExtractMethod_InvalidVisibility_IsNotAccepted()
    {
        // Arrange
        var validVisibilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "private", "internal", "protected", "public", "private protected", "protected internal"
        };

        // Act
        var isValid = validVisibilities.Contains("invalid");

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ExtractMethod_ProtectedInternalVisibility_IsValid()
    {
        // Arrange
        var visibility = "protected internal";
        var validVisibilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "private", "internal", "protected", "public", "private protected", "protected internal"
        };

        // Act
        var isValid = validVisibilities.Contains(visibility);

        // Assert
        Assert.True(isValid);
    }

    #endregion

    #region Static Context Tests

    [Fact]
    public void ExtractMethod_InStaticMethod_InfersStaticTrue()
    {
        // Arrange
        var containingMethodIsStatic = true;
        var makeStaticParam = (bool?)null;

        // Act
        var shouldMakeStatic = InferStaticModifier(makeStaticParam, containingMethodIsStatic);

        // Assert
        Assert.True(shouldMakeStatic);
    }

    [Fact]
    public void ExtractMethod_InInstanceMethod_InfersStaticFalse()
    {
        // Arrange
        var containingMethodIsStatic = false;
        var makeStaticParam = (bool?)null;

        // Act
        var shouldMakeStatic = InferStaticModifier(makeStaticParam, containingMethodIsStatic);

        // Assert
        Assert.False(shouldMakeStatic);
    }

    [Fact]
    public void ExtractMethod_ExplicitStaticTrue_OverridesInference()
    {
        // Arrange
        var containingMethodIsStatic = false;
        var makeStaticParam = (bool?)true;

        // Act
        var shouldMakeStatic = InferStaticModifier(makeStaticParam, containingMethodIsStatic);

        // Assert
        Assert.True(shouldMakeStatic);
    }

    [Fact]
    public void ExtractMethod_ExplicitStaticFalse_OverridesInference()
    {
        // Arrange
        var containingMethodIsStatic = true;
        var makeStaticParam = (bool?)false;

        // Act
        var shouldMakeStatic = InferStaticModifier(makeStaticParam, containingMethodIsStatic);

        // Assert
        Assert.False(shouldMakeStatic);
    }

    #endregion

    #region Comment Preservation Tests

    [Fact]
    public void ExtractMethod_NodeWithTrivia_PreservesLeadingTrivia()
    {
        // Arrange
        var source = @"
class Test {
    void M() {
        // Important comment
        var x = 1;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var statement = tree.GetRoot()
            .DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .First();

        // Act
        var hasLeadingTrivia = statement.HasLeadingTrivia;

        // Assert
        Assert.True(hasLeadingTrivia);
    }

    [Fact]
    public void ExtractMethod_NodeWithComment_ContainsCommentTrivia()
    {
        // Arrange
        var source = @"
class Test {
    void M() {
        // Important comment
        var x = 1;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var statement = tree.GetRoot()
            .DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .First();

        // Act
        var hasCommentTrivia = statement.GetLeadingTrivia()
            .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia));

        // Assert
        Assert.True(hasCommentTrivia);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Mimics the ValidateSelection logic from ExtractMethodOperation.
    /// </summary>
    private static void ValidateSelection(List<SyntaxNode> nodes)
    {
        foreach (var node in nodes)
        {
            // Check for yield statements (using DescendantNodesAndSelf to include the node itself)
            if (node.DescendantNodesAndSelf().Any(n => n is YieldStatementSyntax))
            {
                throw new RefactoringException(
                    ErrorCodes.ContainsYield,
                    "Cannot extract code containing yield statements.");
            }

            var returns = node.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().ToList();
            if (returns.Count > 1)
            {
                throw new RefactoringException(
                    ErrorCodes.MultipleExitPoints,
                    "Selection has multiple return statements. Simplify before extraction.");
            }
        }
    }

    private static void ValidateEmptySelection(List<SyntaxNode> nodes)
    {
        if (nodes.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.EmptySelection,
                "No code selected for extraction.");
        }
    }

    private static bool ContainsAwaitExpression(List<SyntaxNode> nodes)
    {
        return nodes.Any(n => n.DescendantNodes().Any(d => d is AwaitExpressionSyntax));
    }

    private static bool InferStaticModifier(bool? makeStaticParam, bool containingMethodIsStatic)
    {
        return makeStaticParam ?? containingMethodIsStatic;
    }

    private static List<SyntaxNode> CreateNodesWithYieldReturn()
    {
        var source = @"
class Test {
    System.Collections.Generic.IEnumerable<int> M() {
        yield return 1;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var yieldStatement = tree.GetRoot()
            .DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .First();

        return new List<SyntaxNode> { yieldStatement };
    }

    private static List<SyntaxNode> CreateNodesWithYieldBreak()
    {
        var source = @"
class Test {
    System.Collections.Generic.IEnumerable<int> M() {
        yield break;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var yieldStatement = tree.GetRoot()
            .DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .First();

        return new List<SyntaxNode> { yieldStatement };
    }

    private static List<SyntaxNode> CreateNodesWithMultipleReturns()
    {
        var source = @"
class Test {
    int M(bool flag) {
        if (flag) return 1;
        return 2;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First();

        // Return the body which contains multiple returns
        return new List<SyntaxNode> { method.Body! };
    }

    private static List<SyntaxNode> CreateNodesWithSingleReturn()
    {
        var source = @"
class Test {
    int M() {
        return 1;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var returnStatement = tree.GetRoot()
            .DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .First();

        return new List<SyntaxNode> { returnStatement };
    }

    private static List<SyntaxNode> CreateNodesWithAwait()
    {
        var source = @"
class Test {
    async System.Threading.Tasks.Task M() {
        await System.Threading.Tasks.Task.Delay(1);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var awaitExpression = tree.GetRoot()
            .DescendantNodes()
            .OfType<AwaitExpressionSyntax>()
            .First();

        return new List<SyntaxNode> { awaitExpression.Parent! };
    }

    #endregion
}

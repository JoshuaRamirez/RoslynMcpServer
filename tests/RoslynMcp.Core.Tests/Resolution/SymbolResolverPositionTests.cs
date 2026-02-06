using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using Xunit;

namespace RoslynMcp.Core.Tests.Resolution;

/// <summary>
/// Tests for SymbolResolver.GetPosition static helper.
/// </summary>
public class SymbolResolverPositionTests
{
    [Fact]
    public void GetPosition_FirstLineFirstColumn_ReturnsZero()
    {
        var root = CSharpSyntaxTree.ParseText("class C { }").GetRoot();
        var position = SymbolResolver.GetPosition(root, line: 1, column: 1);
        Assert.Equal(0, position);
    }

    [Fact]
    public void GetPosition_SecondLine_ReturnsCorrectOffset()
    {
        var source = "class C\n{\n}";
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        // Line 2, column 1 = position of '{' (after "class C\n")
        var position = SymbolResolver.GetPosition(root, line: 2, column: 1);
        Assert.Equal(8, position); // "class C\n" = 8 chars
    }

    [Fact]
    public void GetPosition_LineOutOfRange_ThrowsException()
    {
        var root = CSharpSyntaxTree.ParseText("class C { }").GetRoot();

        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolResolver.GetPosition(root, line: 100, column: 1));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void GetPosition_ColumnOutOfRange_ThrowsException()
    {
        var root = CSharpSyntaxTree.ParseText("class C { }").GetRoot();

        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolResolver.GetPosition(root, line: 1, column: 500));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void GetPosition_ZeroLine_ThrowsException()
    {
        var root = CSharpSyntaxTree.ParseText("class C { }").GetRoot();

        var ex = Assert.Throws<RefactoringException>(() =>
            SymbolResolver.GetPosition(root, line: 0, column: 1));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void GetPosition_ColumnAtEndOfLine_Succeeds()
    {
        var source = "class C { }";
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        // Column at end of line should work (column = length + 1 for EOL position)
        var position = SymbolResolver.GetPosition(root, line: 1, column: source.Length + 1);
        Assert.Equal(source.Length, position);
    }
}

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="AnalyzeDataFlowOperation"/>,
/// including optional <c>startColumn</c> / <c>endColumn</c> region trim.
/// </summary>
public class AnalyzeDataFlowOperationTests
{
    // Closing """ is at column 0 so raw-string dedent does not re-indent.
    // Two statements share one line so omitted columns include both and
    // start+end columns can trim to one neighbor.
    private const string SameLineNeighborSource =
        """
class C
{
void M()
{
int a = 1; return a;
}
}
""";

    #region Input Validation

    [Fact]
    public void Columns_DefaultToNull()
    {
        var @params = new AnalyzeDataFlowParams
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = 1,
            EndLine = 5
        };

        Assert.Null(@params.StartColumn);
        Assert.Null(@params.EndColumn);
    }

    [Fact]
    public void Validate_InvalidStartColumn_ThrowsInvalidColumnNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                EndLine = 5,
                StartColumn = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("startColumn must be >= 1.", ex.Message);
    }

    [Fact]
    public void Validate_InvalidEndColumn_ThrowsInvalidColumnNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                EndLine = 5,
                EndColumn = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("endColumn must be >= 1.", ex.Message);
    }

    [Fact]
    public void Validate_NegativeEndColumn_ThrowsInvalidColumnNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                EndLine = 5,
                EndColumn = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_SameLineStartColumnAfterEndColumn_ThrowsInvalidRegion()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 5,
                EndLine = 5,
                StartColumn = 12,
                EndColumn = 3
            }));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidStartLine_UnchangedInvalidLineNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                EndLine = 5,
                StartColumn = 1,
                EndColumn = 8
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySourceFile_WithColumns_ThrowsMissingRequiredParam(string sourceFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.Validate(new AnalyzeDataFlowParams
            {
                SourceFile = sourceFile,
                StartLine = 1,
                EndLine = 5,
                StartColumn = 1,
                EndColumn = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    #endregion

    #region Span construction (no workspace)

    [Fact]
    public void BuildRegionSpan_OmittedColumns_PreservesWholeLine()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);
        var omitted = AnalyzeDataFlowOperation.BuildRegionSpan(text, Params(line, line));
        var nullColumns = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn: null, endColumn: null));

        var expected = TextSpan.FromBounds(text.Lines[line - 1].Start, text.Lines[line - 1].End);
        Assert.Equal(expected, omitted);
        Assert.Equal(expected, nullColumns);
        Assert.Equal(omitted, nullColumns);
    }

    [Fact]
    public void BuildRegionSpan_OmittedColumns_DoesNotForceColumn1()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);
        var omitted = AnalyzeDataFlowOperation.BuildRegionSpan(text, Params(line, line));
        var forcedColumn1 = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn: 1, endColumn: 1));

        Assert.NotEqual(omitted, forcedColumn1);
        Assert.Equal(text.Lines[line - 1].Start, omitted.Start);
        Assert.Equal(text.Lines[line - 1].End, omitted.End);
        Assert.Equal(0, forcedColumn1.Length);
    }

    [Fact]
    public void BuildRegionSpan_StartAndEndColumns_TrimVsSameLineNeighbor()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);

        var omitted = AnalyzeDataFlowOperation.BuildRegionSpan(text, Params(line, line));
        Assert.True(omitted.Contains(local.Span));
        Assert.True(omitted.Contains(ret.Span));

        var firstOnly = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, ColumnOf(local, start: true), ColumnOf(local, start: false)));
        Assert.True(firstOnly.Contains(local.Span));
        Assert.False(firstOnly.Contains(ret.Span));

        var secondOnly = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, ColumnOf(ret, start: true), ColumnOf(ret, start: false)));
        Assert.False(secondOnly.Contains(local.Span));
        Assert.True(secondOnly.Contains(ret.Span));
    }

    [Fact]
    public void BuildRegionSpan_OneColumnOmitted_KeepsStartOfLineOrEndOfLine()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);

        var startSetEndOmitted = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn: ColumnOf(ret, start: true)));
        Assert.Equal(text.Lines[line - 1].Start + (ColumnOf(ret, start: true) - 1), startSetEndOmitted.Start);
        Assert.Equal(text.Lines[line - 1].End, startSetEndOmitted.End);
        Assert.False(startSetEndOmitted.Contains(local.Span));
        Assert.True(startSetEndOmitted.Contains(ret.Span));

        var startOmittedEndSet = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, endColumn: ColumnOf(local, start: false)));
        Assert.Equal(text.Lines[line - 1].Start, startOmittedEndSet.Start);
        Assert.Equal(text.Lines[line - 1].Start + (ColumnOf(local, start: false) - 1), startOmittedEndSet.End);
        Assert.True(startOmittedEndSet.Contains(local.Span));
        Assert.False(startOmittedEndSet.Contains(ret.Span));
    }

    [Fact]
    public void BuildRegionSpan_PartialStatementColumns_DoNotContainStatement()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);
        var ident = local.Declaration.Variables[0].Identifier;
        var identSpan = ident.GetLocation().GetLineSpan();
        var startColumn = identSpan.StartLinePosition.Character + 1;
        var endColumn = identSpan.EndLinePosition.Character + 1;

        var partial = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn, endColumn));

        Assert.False(partial.Contains(local.Span));
        Assert.False(partial.Contains(ret.Span));
    }

    [Fact]
    public void BuildRegionSpan_Crlf_OmittedColumnsStillWholeLine()
    {
        var source = SameLineNeighborSource.Replace("\n", "\r\n", StringComparison.Ordinal);
        var text = SourceText.From(source);
        var line = StatementLine(source, out var local, out var ret);
        var omitted = AnalyzeDataFlowOperation.BuildRegionSpan(text, Params(line, line));

        Assert.Equal(text.Lines[line - 1].Start, omitted.Start);
        Assert.Equal(text.Lines[line - 1].End, omitted.End);
        Assert.True(omitted.Contains(local.Span));
        Assert.True(omitted.Contains(ret.Span));

        var firstOnly = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, ColumnOf(local, start: true), ColumnOf(local, start: false)));
        Assert.True(firstOnly.Contains(local.Span));
        Assert.False(firstOnly.Contains(ret.Span));
    }

    [Fact]
    public void BuildRegionSpan_InvertedAbsolutePositions_ThrowsInvalidRegion()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);

        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.BuildRegionSpan(
                text,
                Params(line, line, startColumn: 8, endColumn: 2)));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
    }

    [Fact]
    public void BuildRegionSpan_StartColumnPastLineEnd_ThrowsInvalidColumnNumber()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);
        var lineInfo = text.Lines[line - 1];
        var pastEnd = (lineInfo.End - lineInfo.Start) + 2;

        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.BuildRegionSpan(
                text,
                Params(line, line, startColumn: pastEnd)));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRegionSpan_EndColumnPastLineEnd_ThrowsInvalidColumnNumber()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);
        var lineInfo = text.Lines[line - 1];
        var pastEnd = (lineInfo.End - lineInfo.Start) + 2;

        var ex = Assert.Throws<RefactoringException>(() =>
            AnalyzeDataFlowOperation.BuildRegionSpan(
                text,
                Params(line, line, endColumn: pastEnd)));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRegionSpan_EndColumnAtTextLineEnd_EqualsOmittedEnd()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out _, out _);
        var lineInfo = text.Lines[line - 1];
        var exclusiveEndColumn = (lineInfo.End - lineInfo.Start) + 1;

        var omitted = AnalyzeDataFlowOperation.BuildRegionSpan(text, Params(line, line));
        var atLineEnd = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, endColumn: exclusiveEndColumn));

        Assert.Equal(omitted, atLineEnd);
        Assert.Equal(lineInfo.End, atLineEnd.End);
    }

    #endregion

    #region Operation execution

    [SkippableFact]
    public async Task AnalyzeDataFlow_OmittedColumns_IncludesSameLineNeighbor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out _, out _);

        var omitted = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });
        var nullColumns = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = null,
            EndColumn = null
        });

        Assert.True(omitted.Success);
        Assert.True(nullColumns.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(nullColumns.Data);
        Assert.Contains("a", omitted.Data.WrittenInside);
        Assert.Contains("a", omitted.Data.ReadInside);
        Assert.Equal(omitted.Data.WrittenInside, nullColumns.Data.WrittenInside);
        Assert.Equal(omitted.Data.ReadInside, nullColumns.Data.ReadInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_StartAndEndColumns_TrimVsSameLineNeighbor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);

        var firstOnly = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = ColumnOf(local, start: true),
            EndColumn = ColumnOf(local, start: false)
        });
        var secondOnly = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = ColumnOf(ret, start: true),
            EndColumn = ColumnOf(ret, start: false)
        });

        Assert.True(firstOnly.Success);
        Assert.True(secondOnly.Success);
        Assert.NotNull(firstOnly.Data);
        Assert.NotNull(secondOnly.Data);
        Assert.Contains("a", firstOnly.Data.WrittenInside);
        Assert.DoesNotContain("a", firstOnly.Data.ReadInside);
        Assert.Contains("a", secondOnly.Data.ReadInside);
        Assert.DoesNotContain("a", secondOnly.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_PartialStatementColumns_StayInvalidRegion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out var local, out _);
        var ident = local.Declaration.Variables[0].Identifier;
        var identSpan = ident.GetLocation().GetLineSpan();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = line,
                EndLine = line,
                StartColumn = identSpan.StartLinePosition.Character + 1,
                EndColumn = identSpan.EndLinePosition.Character + 1
            }));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Contains("No statements found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_ForcedColumn1BothEnds_StaysInvalidRegion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out _, out _);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = line,
                EndLine = line,
                StartColumn = 1,
                EndColumn = 1
            }));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Contains("No statements found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_Crlf_StartAndEndColumns_TrimVsNeighbor()
    {
        var source = SameLineNeighborSource.Replace("\n", "\r\n", StringComparison.Ordinal);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);
        var line = StatementLine(source, out var local, out _);

        var firstOnly = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = ColumnOf(local, start: true),
            EndColumn = ColumnOf(local, start: false)
        });

        Assert.True(firstOnly.Success);
        Assert.NotNull(firstOnly.Data);
        Assert.Contains("a", firstOnly.Data.WrittenInside);
        Assert.DoesNotContain("a", firstOnly.Data.ReadInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_InvalidColumn_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = 1,
                EndLine = 1,
                StartColumn = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnalyzeDataFlow_EmptySourceFile_WithColumns_ThrowsMissingRequiredParam(string sourceFile)
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = sourceFile,
                StartLine = 1,
                EndLine = 5,
                StartColumn = 1,
                EndColumn = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    // Fixture for sibling-list filter parity with analyze_control_flow (#931).
    // Whole-method / braces+body regions contain BlockSyntax + nested
    // statements; without Parent is Block/SwitchSection filter, First/Last
    // hit ValidateStatementRange.
    private const string BracedBlockSource =
        """
class C
{
    int x = 1;
    int ExprBody() => x + 2;
    int Prop => x;
    void Block()
    {
        var y = x;
        System.Console.Write(y);
    }
}
""";

    [SkippableFact]
    public async Task AnalyzeDataFlow_ExpressionBodiedMethod_SucceedsViaExpressionOverload()
    {
        // Region = whole ExprBody line (`int ExprBody() => x + 2;`).
        // Zero StatementSyntax → expression fallback uses ArrowExpression
        // `x + 2` via AnalyzeDataFlow(ExpressionSyntax).
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ExprBody");
        var methodSpan = method.GetLocation().GetLineSpan();
        var line = methodSpan.StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("this", result.Data.ReadInside);
        Assert.Contains("this", result.Data.DataFlowsIn);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_ExpressionBodiedProperty_SucceedsViaExpressionOverload()
    {
        // Region = whole Prop line (`int Prop => x;`). Zero StatementSyntax
        // → expression fallback on ArrowExpression `x`.
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var prop = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "Prop");
        var propSpan = prop.GetLocation().GetLineSpan();
        var line = propSpan.StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("this", result.Data.ReadInside);
        Assert.Contains("this", result.Data.DataFlowsIn);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_MultiArrowExpressionRegion_ThrowsInvalidRegion()
    {
        // Region spans both ExprBody and Prop lines — two fully contained
        // ArrowExpressionClause expressions. Must be InvalidRegion (not
        // silently analyzing only the first via FirstOrDefault).
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "ExprBody");
        var prop = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "Prop");
        var startLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = prop.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = startLine,
                EndLine = endLine
            }));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Contains("multiple", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arrow", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_InnerStatementsOnly_Succeeds()
    {
        // Region = inner statements only (no enclosing BlockSyntax).
        // Regression: still succeeds after sibling-list filter.
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Block");
        var firstStmt = method.Body!.Statements.First();
        var lastStmt = method.Body!.Statements.Last();
        var startLine = firstStmt.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = lastStmt.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = startLine,
            EndLine = endLine
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("y", result.Data.WrittenInside);
        Assert.Contains("y", result.Data.ReadInside);
        Assert.Contains("this", result.Data.ReadInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_WholeMethodIncludingBraces_SucceedsWithSiblingFilter()
    {
        // Region = whole Block method (signature through closing brace).
        // Contained StatementSyntax includes enclosing Block + nested
        // statements (mixed parents). Sibling-list filter keeps only the
        // statement-list children so AnalyzeDataFlow succeeds.
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Block");
        var methodSpan = method.GetLocation().GetLineSpan();
        var startLine = methodSpan.StartLinePosition.Line + 1;
        var endLine = methodSpan.EndLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = startLine,
            EndLine = endLine
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("y", result.Data.WrittenInside);
        Assert.Contains("y", result.Data.ReadInside);
        Assert.Contains("this", result.Data.ReadInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_BracesAndBodyOnly_SucceedsWithSiblingFilter()
    {
        // Region = braces + body only (previously failed: Block + nested).
        await using var workspace = await TempWorkspace.CreateAsync(BracedBlockSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(BracedBlockSource);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Block");
        var bodySpan = method.Body!.GetLocation().GetLineSpan();
        var startLine = bodySpan.StartLinePosition.Line + 1;
        var endLine = bodySpan.EndLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = startLine,
            EndLine = endLine
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("y", result.Data.WrittenInside);
        Assert.Contains("y", result.Data.ReadInside);
        Assert.Contains("this", result.Data.ReadInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_UnbracedEmbeddedSingleStatement_AnalyzesAlone()
    {
        // Region = only the unbraced return under if. Parent is
        // IfStatementSyntax, not Block/SwitchSection, so the sibling-list
        // filter yields empty. Exactly one contained StatementSyntax →
        // analyze that statement alone (first==last).
        const string source =
            """
class C
{
public int M(bool flag)
{
if (flag)
return 1;
return 0;
}
}
""";

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var embeddedReturn = root.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Single(r => r.Parent is IfStatementSyntax);
        var returnSpan = embeddedReturn.GetLocation().GetLineSpan();
        var line = returnSpan.StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // Literal return has no named locals; success without RoslynError is the contract.
        Assert.NotNull(result.Data.ReadInside);
        Assert.NotNull(result.Data.WrittenInside);
    }

    // Fixture for EqualsValueClause initializer fallback (#933).
    // Field/const/static lines are not StatementSyntax; local Value-only
    // column trim does not fully contain LocalDeclarationStatement.
    private const string InitializerSource =
        """
class C
{
    const int K = 10;
    static int S = K + 1;
    int f = K + 2;
    void M(int p)
    {
        int z = S + p;
        System.Console.Write(z);
    }
}
""";

    [SkippableFact]
    public async Task AnalyzeDataFlow_FieldInitializerWholeLine_SucceedsViaEqualsValueClause()
    {
        // Region = whole field line (`int f = K + 2;`). Zero StatementSyntax
        // → EqualsValueClause.Value fallback via AnalyzeDataFlow(ExpressionSyntax).
        await using var workspace = await TempWorkspace.CreateAsync(InitializerSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(InitializerSource);
        var root = tree.GetRoot();
        var field = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "f"));
        var line = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.ReadInside);
        Assert.NotNull(result.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_StaticFieldInitializerWholeLine_SucceedsViaEqualsValueClause()
    {
        // Region = whole static field line (`static int S = K + 1;`).
        await using var workspace = await TempWorkspace.CreateAsync(InitializerSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(InitializerSource);
        var root = tree.GetRoot();
        var field = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "S"));
        var line = field.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.ReadInside);
        Assert.NotNull(result.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_LocalInitializerValueOnlyColumns_SucceedsViaEqualsValueClause()
    {
        // Region = column-trimmed EqualsValueClause.Value (`S + p`) inside
        // `int z = S + p;`. LocalDeclarationStatement is NOT fully contained
        // → expression fallback; ReadInside/DataFlowsIn include p.
        await using var workspace = await TempWorkspace.CreateAsync(InitializerSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(InitializerSource);
        var root = tree.GetRoot();
        var local = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Single();
        var value = local.Declaration.Variables[0].Initializer!.Value;
        var valueSpan = value.GetLocation().GetLineSpan();
        var line = valueSpan.StartLinePosition.Line + 1;
        var startColumn = valueSpan.StartLinePosition.Character + 1;
        var endColumn = valueSpan.EndLinePosition.Character + 1;

        // Sanity: Value-only span does not contain the full local declaration.
        var text = SourceText.From(InitializerSource);
        var region = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn, endColumn));
        Assert.False(region.Contains(local.Span));
        Assert.True(region.Contains(value.Span));

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = startColumn,
            EndColumn = endColumn
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("p", result.Data.ReadInside);
        Assert.Contains("p", result.Data.DataFlowsIn);
        // Expression path does not treat the local declarator as WrittenInside.
        Assert.DoesNotContain("z", result.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_LocalDeclarationWholeLine_StillSucceedsViaStatementPath()
    {
        // Region = whole `int z = S + p;` line. LocalDeclarationStatement is
        // fully contained → statement path regression (WrittenInside includes z).
        await using var workspace = await TempWorkspace.CreateAsync(InitializerSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(InitializerSource);
        var root = tree.GetRoot();
        var local = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Single();
        var line = local.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("p", result.Data.ReadInside);
        Assert.Contains("p", result.Data.DataFlowsIn);
        Assert.Contains("z", result.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_LambdaInitializerWithParameterDefault_IgnoresNestedParameterEquals()
    {
        // C# 12+ style: declaration initializer Value is a lambda that itself
        // contains a ParameterSyntax default EqualsValueClause. Without filtering
        // Parameter parents, Count>1 rejects a single declaration initializer.
        const string source =
            """
class C
{
    void M()
    {
        System.Func<int, int> f = (int x = 1) => x;
        System.Console.Write(f(2));
    }
}
""";

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var local = root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .Single();
        var line = local.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        // Whole local line contains LocalDeclarationStatement → statement path.
        // Use Value-only columns so EqualsValueClause fallback is exercised.
        var value = local.Declaration.Variables[0].Initializer!.Value;
        var valueSpan = value.GetLocation().GetLineSpan();
        var startColumn = valueSpan.StartLinePosition.Character + 1;
        var endColumn = valueSpan.EndLinePosition.Character + 1;

        var text = SourceText.From(source);
        var region = AnalyzeDataFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn, endColumn));
        Assert.False(region.Contains(local.Span));
        Assert.True(region.Contains(value.Span));
        // Nested parameter default EqualsValueClause is also fully contained.
        var paramDefault = root.DescendantNodes()
            .OfType<EqualsValueClauseSyntax>()
            .Single(c => c.Parent is ParameterSyntax);
        Assert.True(region.Contains(paramDefault.Value.Span));

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = startColumn,
            EndColumn = endColumn
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.ReadInside);
        Assert.NotNull(result.Data.WrittenInside);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_MultiEqualsValueClauseRegion_ThrowsInvalidRegion()
    {
        // Region spans static S and field f lines — two fully contained
        // EqualsValueClause.Values, zero statements, zero arrows.
        // Must be InvalidRegion (ambiguous), same spirit as multi-arrow.
        await using var workspace = await TempWorkspace.CreateAsync(InitializerSource);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(InitializerSource);
        var root = tree.GetRoot();
        var staticField = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "S"));
        var instanceField = root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "f"));
        var startLine = staticField.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = instanceField.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeDataFlowParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = startLine,
                EndLine = endLine
            }));

        Assert.Equal(ErrorCodes.InvalidRegion, ex.ErrorCode);
        Assert.Contains("multiple", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EqualsValueClause", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AnalyzeDataFlow_TrailingNestedBlock_RestrictsToOneStatementListParent()
    {
        // Method ending in if { return }: parent-type filter alone would
        // keep outer-block statements AND the nested return (different
        // parents) so First/Last fail ValidateStatementRange. One-parent
        // restriction keeps only the outermost contained list.
        const string source =
            """
class C
{
    void M(bool flag, int x)
    {
        var y = x;
        System.Console.Write(y);
        if (flag)
        {
            return;
        }
    }
}
""";

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AnalyzeDataFlowOperation(workspace.Context);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "M");
        var methodSpan = method.GetLocation().GetLineSpan();
        var startLine = methodSpan.StartLinePosition.Line + 1;
        var endLine = methodSpan.EndLinePosition.Line + 1;

        var result = await operation.ExecuteAsync(new AnalyzeDataFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = startLine,
            EndLine = endLine
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains("y", result.Data.WrittenInside);
        Assert.Contains("y", result.Data.ReadInside);
        Assert.Contains("x", result.Data.ReadInside);
        Assert.Contains("flag", result.Data.ReadInside);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpAnalyzeDataFlow_" + name);

    private static AnalyzeDataFlowParams Params(
        int startLine,
        int endLine,
        int? startColumn = null,
        int? endColumn = null) =>
        new()
        {
            SourceFile = AbsoluteTestPath(),
            StartLine = startLine,
            EndLine = endLine,
            StartColumn = startColumn,
            EndColumn = endColumn
        };

    private static int StatementLine(
        string source,
        out LocalDeclarationStatementSyntax local,
        out ReturnStatementSyntax ret)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        local = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().Single();
        ret = root.DescendantNodes().OfType<ReturnStatementSyntax>().Single();
        return local.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    private static int ColumnOf(CSharpSyntaxNode node, bool start)
    {
        var span = node.GetLocation().GetLineSpan();
        return start
            ? span.StartLinePosition.Character + 1
            : span.EndLinePosition.Character + 1;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAnalyzeDataFlow_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Foo.cs");

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = sourcePath,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }

                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch
                {
                    // ignore locked temp files
                }
            });
        }
    }

    #endregion
}

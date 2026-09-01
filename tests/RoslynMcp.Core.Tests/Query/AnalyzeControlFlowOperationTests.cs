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
/// Operation-level tests for <see cref="AnalyzeControlFlowOperation"/>,
/// including optional <c>startColumn</c> / <c>endColumn</c> region trim.
/// </summary>
public class AnalyzeControlFlowOperationTests
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
        var @params = new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
            AnalyzeControlFlowOperation.Validate(new AnalyzeControlFlowParams
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
        var omitted = AnalyzeControlFlowOperation.BuildRegionSpan(text, Params(line, line));
        var nullColumns = AnalyzeControlFlowOperation.BuildRegionSpan(
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
        var omitted = AnalyzeControlFlowOperation.BuildRegionSpan(text, Params(line, line));
        var forcedColumn1 = AnalyzeControlFlowOperation.BuildRegionSpan(
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

        var omitted = AnalyzeControlFlowOperation.BuildRegionSpan(text, Params(line, line));
        Assert.True(omitted.Contains(local.Span));
        Assert.True(omitted.Contains(ret.Span));

        var firstOnly = AnalyzeControlFlowOperation.BuildRegionSpan(
            text, Params(line, line, ColumnOf(local, start: true), ColumnOf(local, start: false)));
        Assert.True(firstOnly.Contains(local.Span));
        Assert.False(firstOnly.Contains(ret.Span));

        var secondOnly = AnalyzeControlFlowOperation.BuildRegionSpan(
            text, Params(line, line, ColumnOf(ret, start: true), ColumnOf(ret, start: false)));
        Assert.False(secondOnly.Contains(local.Span));
        Assert.True(secondOnly.Contains(ret.Span));
    }

    [Fact]
    public void BuildRegionSpan_OneColumnOmitted_KeepsStartOfLineOrEndOfLine()
    {
        var text = SourceText.From(SameLineNeighborSource);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);

        var startSetEndOmitted = AnalyzeControlFlowOperation.BuildRegionSpan(
            text, Params(line, line, startColumn: ColumnOf(ret, start: true)));
        Assert.Equal(text.Lines[line - 1].Start + (ColumnOf(ret, start: true) - 1), startSetEndOmitted.Start);
        Assert.Equal(text.Lines[line - 1].End, startSetEndOmitted.End);
        Assert.False(startSetEndOmitted.Contains(local.Span));
        Assert.True(startSetEndOmitted.Contains(ret.Span));

        var startOmittedEndSet = AnalyzeControlFlowOperation.BuildRegionSpan(
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

        var partial = AnalyzeControlFlowOperation.BuildRegionSpan(
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
        var omitted = AnalyzeControlFlowOperation.BuildRegionSpan(text, Params(line, line));

        Assert.Equal(text.Lines[line - 1].Start, omitted.Start);
        Assert.Equal(text.Lines[line - 1].End, omitted.End);
        Assert.True(omitted.Contains(local.Span));
        Assert.True(omitted.Contains(ret.Span));

        var firstOnly = AnalyzeControlFlowOperation.BuildRegionSpan(
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
            AnalyzeControlFlowOperation.BuildRegionSpan(
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
            AnalyzeControlFlowOperation.BuildRegionSpan(
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
            AnalyzeControlFlowOperation.BuildRegionSpan(
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

        var omitted = AnalyzeControlFlowOperation.BuildRegionSpan(text, Params(line, line));
        var atLineEnd = AnalyzeControlFlowOperation.BuildRegionSpan(
            text, Params(line, line, endColumn: exclusiveEndColumn));

        Assert.Equal(omitted, atLineEnd);
        Assert.Equal(lineInfo.End, atLineEnd.End);
    }

    #endregion

    #region Operation execution

    [SkippableFact]
    public async Task AnalyzeControlFlow_OmittedColumns_IncludesSameLineNeighbor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out _, out _);

        var omitted = await operation.ExecuteAsync(new AnalyzeControlFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line
        });
        var nullColumns = await operation.ExecuteAsync(new AnalyzeControlFlowParams
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
        Assert.NotEmpty(omitted.Data.ReturnStatements);
        Assert.Equal(omitted.Data.ReturnStatements.Count, nullColumns.Data.ReturnStatements.Count);
        Assert.Equal(omitted.Data.EndPointReachable, nullColumns.Data.EndPointReachable);
        Assert.Contains(omitted.Data.ReturnStatements, r => r.Kind == "Return");
    }

    [SkippableFact]
    public async Task AnalyzeControlFlow_StartAndEndColumns_TrimVsSameLineNeighbor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out var local, out var ret);

        var firstOnly = await operation.ExecuteAsync(new AnalyzeControlFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = ColumnOf(local, start: true),
            EndColumn = ColumnOf(local, start: false)
        });
        var secondOnly = await operation.ExecuteAsync(new AnalyzeControlFlowParams
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
        Assert.Empty(firstOnly.Data.ReturnStatements);
        Assert.True(firstOnly.Data.EndPointReachable);
        Assert.NotEmpty(secondOnly.Data.ReturnStatements);
        Assert.Contains(secondOnly.Data.ReturnStatements, r => r.Kind == "Return");
    }

    [SkippableFact]
    public async Task AnalyzeControlFlow_PartialStatementColumns_StayInvalidRegion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out var local, out _);
        var ident = local.Declaration.Variables[0].Identifier;
        var identSpan = ident.GetLocation().GetLineSpan();

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeControlFlowParams
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
    public async Task AnalyzeControlFlow_ForcedColumn1BothEnds_StaysInvalidRegion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);
        var line = StatementLine(SameLineNeighborSource, out _, out _);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeControlFlowParams
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
    public async Task AnalyzeControlFlow_Crlf_StartAndEndColumns_TrimVsNeighbor()
    {
        var source = SameLineNeighborSource.Replace("\n", "\r\n", StringComparison.Ordinal);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);
        var line = StatementLine(source, out var local, out _);

        var firstOnly = await operation.ExecuteAsync(new AnalyzeControlFlowParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = line,
            EndLine = line,
            StartColumn = ColumnOf(local, start: true),
            EndColumn = ColumnOf(local, start: false)
        });

        Assert.True(firstOnly.Success);
        Assert.NotNull(firstOnly.Data);
        Assert.Empty(firstOnly.Data.ReturnStatements);
        Assert.True(firstOnly.Data.EndPointReachable);
    }

    [SkippableFact]
    public async Task AnalyzeControlFlow_InvalidColumn_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeControlFlowParams
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
    public async Task AnalyzeControlFlow_EmptySourceFile_WithColumns_ThrowsMissingRequiredParam(string sourceFile)
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNeighborSource);
        var operation = new AnalyzeControlFlowOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AnalyzeControlFlowParams
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

    #region Helpers

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpAnalyzeControlFlow_" + name);

    private static AnalyzeControlFlowParams Params(
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAnalyzeControlFlow_" + Guid.NewGuid().ToString("N"));
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

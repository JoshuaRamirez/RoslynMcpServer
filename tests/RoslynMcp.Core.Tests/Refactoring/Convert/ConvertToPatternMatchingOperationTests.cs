using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="ConvertToPatternMatchingOperation"/> (UC-CV5 column).
/// </summary>
public class ConvertToPatternMatchingOperationTests
{
    private const string SingleSwitchSource = """
        namespace TestApp;

        public class Classifier
        {
            public string Describe(object value)
            {
                switch (value)
                {
                    case 1: return "one";
                    default: return "other";
                }
            }
        }
        """;

    private const string SingleIfSource = """
        namespace TestApp;

        public class Classifier
        {
            public string Describe(object value)
            {
                if (value is int i)
                    return "int";
                else
                    return "other";
            }
        }
        """;

    private const string SameLineIfThenSwitchSource = """
        namespace TestApp;

        public class Pair
        {
            public string Describe(object a, object b)
            {
                if (a is int i) return "intA"; else return "otherA"; switch (b) { case 1: return "one"; default: return "otherB"; }
            }
        }
        """;

    private const string SameLineTwoIfsSource = """
        namespace TestApp;

        public class Pair
        {
            public string Describe(object value)
            {
                if (value is int i) return "int"; else return "other"; if (value is string s) return "string"; else return "other";
            }
        }
        """;

    private const string AdjacentIfsSource = """
        namespace TestApp;

        public class Adjacent
        {
            public string Describe(object a, object b)
            {
                if (a is int i) return "intA"; else return "otherA";if (b is string s) return "stringB"; else return "otherB";
            }
        }
        """;

    private const string ContinuationIfSource = """
        namespace TestApp;

        public class Split
        {
            public string Describe(object value)
            {
                if
                    (value is int i)
                    return "int";
                else
                    return "other";
            }
        }
        """;

    private const string RejectNonPatternIfSource = """
        namespace TestApp;

        public class Flag
        {
            public string Describe(bool flag)
            {
                if (flag)
                    return "yes";
                else
                    return "no";
            }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToPatternMatchingOperation.Validate(new ConvertToPatternMatchingParams
            {
                SourceFile = "",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToPatternMatchingOperation.Validate(new ConvertToPatternMatchingParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToPatternMatchingOperation.Validate(new ConvertToPatternMatchingParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToPatternMatchingOperation.Validate(new ConvertToPatternMatchingParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Existing convert / reject cases

    [SkippableFact]
    public async Task Convert_SwitchStatement_RewritesToSwitchExpression()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleSwitchSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleSwitchSource, "switch (value)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("switch (value)", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_IfIsChain_RewritesToSwitchExpression()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleIfSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleIfSource, "if (value is int i)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("if (value is int i)", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_NonPatternIf_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(RejectNonPatternIfSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToPatternMatchingParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(RejectNonPatternIfSource, "if (flag)")
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (flag)", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_NoStatementAtLine_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleIfSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToPatternMatchingParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(SingleIfSource, "namespace TestApp")
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    #endregion

    #region P0 omitted column keeps today's first start-line switch-then-if pick

    [SkippableFact]
    public async Task Convert_OmittedColumn_PrefersSwitchWhenIfAndSwitchShareALine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineIfThenSwitchSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineIfThenSwitchSource, "if (a is int i)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (a is int i)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("switch (b)", updated, StringComparison.Ordinal);
        Assert.Contains("=>", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_OmittedColumn_ConvertsFirstIfOnTheLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineTwoIfsSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineTwoIfsSource, "if (value is int i)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("if (value is int i)", updated, StringComparison.Ordinal);
        Assert.Contains("if (value is string s)", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 column picks the intended statement when two share a line

    [SkippableFact]
    public async Task Convert_Column_SelectsSecondIfOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineTwoIfsSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);
        var line = FindLine(SameLineTwoIfsSource, "if (value is int i)");
        var secondColumn = ColumnOf(SameLineTwoIfsSource, "if (value is string s)");

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("if (value is int i)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("if (value is string s)", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_Column_SelectsIfWhenIfAndSwitchShareALine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineIfThenSwitchSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineIfThenSwitchSource, "if (a is int i)"),
            Column = ColumnOf(SameLineIfThenSwitchSource, "if (a is int i)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("if (a is int i)", updated, StringComparison.Ordinal);
        Assert.Contains("switch (b)", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_ColumnOnContinuationLine_ConvertsThatStatement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ContinuationIfSource);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ContinuationIfSource, "(value is int i)"),
            Column = ColumnOf(ContinuationIfSource, "(value is int i)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("if", updated, StringComparison.Ordinal);
        Assert.Contains("switch", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void FindConvertibleStatement_OmittedColumn_PrefersSwitchThenIf()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineIfThenSwitchSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineIfThenSwitchSource, "if (a is int i)");
        var omitted = ConvertToPatternMatchingOperation.FindConvertibleStatement(root, line, column: null);
        var byIfColumn = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, line, ColumnOf(SameLineIfThenSwitchSource, "if (a is int i)"));
        var bySwitchColumn = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, line, ColumnOf(SameLineIfThenSwitchSource, "switch (b)"));

        Assert.IsType<SwitchStatementSyntax>(omitted);
        Assert.IsType<IfStatementSyntax>(byIfColumn);
        Assert.IsType<SwitchStatementSyntax>(bySwitchColumn);
    }

    [Fact]
    public void FindConvertibleStatement_ColumnPicksSpanCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineTwoIfsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineTwoIfsSource, "if (value is int i)");
        var first = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, line, ColumnOf(SameLineTwoIfsSource, "if (value is int i)"));
        var second = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, line, ColumnOf(SameLineTwoIfsSource, "if (value is string s)"));
        var omitted = ConvertToPatternMatchingOperation.FindConvertibleStatement(root, line, column: null);

        Assert.IsType<IfStatementSyntax>(first);
        Assert.IsType<IfStatementSyntax>(second);
        Assert.IsType<IfStatementSyntax>(omitted);
        Assert.Contains("int", first!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("string", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("string", second!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("int", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(first.ToString(), omitted!.ToString(), StringComparer.Ordinal);
    }

    [Fact]
    public void FindConvertibleStatement_ColumnOnContinuationLine_PicksStatement()
    {
        var tree = CSharpSyntaxTree.ParseText(ContinuationIfSource);
        var root = tree.GetRoot();
        var startLine = FindLine(ContinuationIfSource, "if");
        var continuationLine = FindLine(ContinuationIfSource, "(value is int i)");
        Assert.NotEqual(startLine, continuationLine);

        var omittedOnContinuation = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, continuationLine, column: null);
        var byColumn = ConvertToPatternMatchingOperation.FindConvertibleStatement(
            root, continuationLine, ColumnOf(ContinuationIfSource, "(value is int i)"));

        Assert.Null(omittedOnContinuation);
        Assert.IsType<IfStatementSyntax>(byColumn);
    }

    [Fact]
    public void FindConvertibleStatement_AdjacentStatements_ExclusiveEndDoesNotStealNext()
    {
        var tree = CSharpSyntaxTree.ParseText(AdjacentIfsSource);
        var root = tree.GetRoot();
        var line = FindLine(AdjacentIfsSource, "if (a is int i)");
        var first = root.DescendantNodes().OfType<IfStatementSyntax>()
            .First(statement => statement.ToString().Contains("intA", StringComparison.Ordinal));
        var firstEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondKeyword = ColumnOf(AdjacentIfsSource, "if (b is string s)");

        var atExclusiveEnd = ConvertToPatternMatchingOperation.FindConvertibleStatement(root, line, firstEndCol);
        var atSecondKeyword = ConvertToPatternMatchingOperation.FindConvertibleStatement(root, line, secondKeyword);

        Assert.False(ConvertToPatternMatchingOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.True(atExclusiveEnd == null || !atExclusiveEnd.ToString().Contains("intA", StringComparison.Ordinal));
        Assert.IsType<IfStatementSyntax>(atSecondKeyword);
        Assert.Contains("stringB", atSecondKeyword!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("intA", atSecondKeyword.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { string M(object x) { if (x is int) return \"int\"; else return \"other\"; } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var ifStmt = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().First();
        var span = ifStmt.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ConvertToPatternMatchingOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertToPatternMatchingOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertToPatternMatchingOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertToPatternMatchingOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task Convert_Preview_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleSwitchSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToPatternMatchingOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SingleSwitchSource, "switch (value)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("switch", StringComparison.OrdinalIgnoreCase) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("=>", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static async Task AssertCompilesAsync(TempWorkspace workspace)
    {
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath);
        Assert.NotNull(document);
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToPatternMatchingMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");

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
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToPatternMatching_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

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
                    ProjectPath = projectPath,
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

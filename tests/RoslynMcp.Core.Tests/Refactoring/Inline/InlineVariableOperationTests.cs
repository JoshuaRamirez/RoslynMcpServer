using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Operation-level tests for <see cref="InlineVariableOperation"/> (UC-I2 column leftover).
/// </summary>
public class InlineVariableOperationTests
{
    private const string SimpleSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Run()
            {
                int total = 1 + 2;
                return total;
            }
        }
        """;

    private const string SameLineLocalsSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Run()
            {
                { int value = 1; return value; } { int value = 2; return value; }
            }
        }
        """;

    private const string SplitDeclarationSource = """
        namespace TestApp;

        public class Split
        {
            public int Run(bool inner)
            {
                int // split-decl
                value = 1;
                if (inner)
                {
                    int value = 2;
                    return value;
                }
                return value;
            }
        }
        """;

    private const string ModifiedAfterInitSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Run()
            {
                int total = 1;
                total = 2;
                return total;
            }
        }
        """;

    private const string SameIndentLocalsSource = """
        namespace TestApp;

        public class Worker
        {
            public int Foo()
            {
                int value = 1;
                return value;
            }

            public int Bar()
            {
                int value = 2;
                return value;
            }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineVariableOperation.Validate(new InlineVariableParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = "total",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineVariableOperation.Validate(new InlineVariableParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = "total",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    #endregion

    #region P0 existing inline / preview / modified-after-init still compile

    [SkippableFact]
    public async Task InlineVariable_SimpleLiteral_ReplacesUsagesAndRemovesDeclaration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1 + 2;", updated);
        Assert.DoesNotContain("int total", updated);
        Assert.DoesNotContain("return total;", updated);
    }

    [SkippableFact]
    public async Task InlineVariable_Preview_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("total", result.PendingChanges[0].Description, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_ModifiedAfterInit_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ModifiedAfterInitSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 omitted column keeps today's variableName + line pick

    [SkippableFact]
    public async Task InlineVariable_OmittedColumn_InlinesTheNamedVariable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total",
            Line = FindLine(SimpleSource, "int total = 1 + 2;")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1 + 2;", updated);
        Assert.DoesNotContain("int total", updated);
    }

    [SkippableFact]
    public async Task InlineVariable_OmittedColumn_SameLineLocals_InlinesFirstStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineLocalsSource);
        var operation = new InlineVariableOperation(workspace.Context);
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1;", updated);
        Assert.DoesNotContain("int value = 1;", updated);
        Assert.Contains("int value = 2; return value;", updated);
    }

    #endregion

    #region P0 column picks the intended variable when two share a line

    [SkippableFact]
    public async Task InlineVariable_Column_SelectsSecondLocalOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineLocalsSource);
        var operation = new InlineVariableOperation(workspace.Context);
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");
        var secondColumn = ColumnOf(SameLineLocalsSource, "value = 2");

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int value = 1; return value;", updated);
        Assert.Contains("return 2;", updated);
        Assert.DoesNotContain("int value = 2;", updated);
    }

    [SkippableFact]
    public async Task InlineVariable_Column_SelectsFirstLocalOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineLocalsSource);
        var operation = new InlineVariableOperation(workspace.Context);
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");
        var firstColumn = ColumnOf(SameLineLocalsSource, "value = 1");

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1;", updated);
        Assert.DoesNotContain("int value = 1;", updated);
        Assert.Contains("int value = 2; return value;", updated);
    }

    [SkippableFact]
    public async Task InlineVariable_ColumnOnContinuationLine_InlinesThatVariable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SplitDeclarationSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = FindLine(SplitDeclarationSource, "value = 1;"),
            Column = ColumnOf(SplitDeclarationSource, "value = 1;")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return 1;", updated);
        Assert.DoesNotContain("value = 1;", updated);
        Assert.Contains("int value = 2;", updated);
        Assert.Contains("return value;", updated);
    }

    [SkippableFact]
    public async Task InlineVariable_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineLocalsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");
        var secondColumn = ColumnOf(SameLineLocalsSource, "value = 2");

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindDeclarator_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineLocalsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");
        var first = InlineVariableOperation.FindDeclarator(
            root, "value", line, ColumnOf(SameLineLocalsSource, "value = 1"));
        var second = InlineVariableOperation.FindDeclarator(
            root, "value", line, ColumnOf(SameLineLocalsSource, "value = 2"));
        var omitted = InlineVariableOperation.FindDeclarator(root, "value", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Equal("1", first.Initializer!.Value.ToString());
        Assert.Equal("2", second.Initializer!.Value.ToString());
        Assert.Equal("1", omitted.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindDeclarator_ColumnOnContinuationLine_PicksDeclaration()
    {
        var tree = CSharpSyntaxTree.ParseText(SplitDeclarationSource);
        var root = tree.GetRoot();
        // Single-line snippets only — IndexOf of an LF-only "int\n" missed
        // CRLF checkouts (FindMethod_ColumnOnContinuationLine on #200).
        var typeLine = FindLine(SplitDeclarationSource, "int // split-decl");
        var identifierLine = FindLine(SplitDeclarationSource, "value = 1;");
        Assert.NotEqual(typeLine, identifierLine);

        // Omitted column keeps today's start-line filter when more than one
        // match exists — the split declaration's declarator does not start
        // on the type line. Column on the continuation-line identifier
        // still selects it, including when `line` is the type line.
        var byTypeLineOnly = InlineVariableOperation.FindDeclarator(
            root, "value", typeLine, column: null);
        var byColumnOnIdentifierLine = InlineVariableOperation.FindDeclarator(
            root, "value", identifierLine, ColumnOf(SplitDeclarationSource, "value = 1;"));
        var byColumnOnTypeLine = InlineVariableOperation.FindDeclarator(
            root, "value", typeLine, ColumnOf(SplitDeclarationSource, "value = 1;"));

        Assert.Null(byTypeLineOnly);
        Assert.NotNull(byColumnOnIdentifierLine);
        Assert.Equal("1", byColumnOnIdentifierLine.Initializer!.Value.ToString());
        Assert.NotNull(byColumnOnTypeLine);
        Assert.Equal("1", byColumnOnTypeLine.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindDeclarator_AdjacentLocals_ExclusiveEndDoesNotStealNext()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineLocalsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineLocalsSource, "{ int value = 1; return value; }");
        var first = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(v => v.Identifier.Text == "value");
        var firstDecl = (VariableDeclarationSyntax)first.Parent!;
        var firstEndCol = firstDecl.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondId = ColumnOf(SameLineLocalsSource, "value = 2");

        var atExclusiveEnd = InlineVariableOperation.FindDeclarator(root, "value", line, firstEndCol);
        var atSecondId = InlineVariableOperation.FindDeclarator(root, "value", line, secondId);
        var atFirstId = InlineVariableOperation.FindDeclarator(
            root, "value", line, ColumnOf(SameLineLocalsSource, "value = 1"));

        Assert.False(InlineVariableOperation.SpanCoversColumn(
            firstDecl.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.True(atExclusiveEnd == null || atExclusiveEnd.Initializer!.Value.ToString() != "1");
        Assert.NotNull(atSecondId);
        Assert.Equal("2", atSecondId.Initializer!.Value.ToString());
        Assert.NotNull(atFirstId);
        Assert.Equal("1", atFirstId.Initializer!.Value.ToString());
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { void M() { int value = 1; } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single();
        var span = declarator.Identifier.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(InlineVariableOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(InlineVariableOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(InlineVariableOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(InlineVariableOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task InlineVariable_ColumnWithoutLine_SameIndentLocals_ThrowsSymbolAmbiguous()
    {
        var column = ColumnOf(SameIndentLocalsSource, "value = 1;");
        Assert.Equal(column, ColumnOf(SameIndentLocalsSource, "value = 2;"));

        await using var workspace = await TempWorkspace.CreateAsync(SameIndentLocalsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "value",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpInlineVariableMissing.cs");

    /// <summary>
    /// 1-based line of a single-line snippet. Do not pass a snippet that
    /// embeds <c>\n</c> — CRLF checkouts broke
    /// <c>FindMethod_ColumnOnContinuationLine</c> on #200 when helpers
    /// IndexOf'd an LF-only fragment.
    /// </summary>
    private static int FindLine(string source, string snippet)
    {
        var index = IndexOfSingleLineSnippet(source, snippet);
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>
    /// 1-based column of a single-line snippet. Line start is the character
    /// after the preceding <c>\n</c> (works for LF and CRLF).
    /// </summary>
    private static int ColumnOf(string source, string snippet)
    {
        var index = IndexOfSingleLineSnippet(source, snippet);
        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private static int IndexOfSingleLineSnippet(string source, string snippet)
    {
        if (snippet.Contains('\n') || snippet.Contains('\r'))
            throw new InvalidOperationException("Snippet must be a single line (CRLF-safe).");

        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return index;
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineVariable_" + Guid.NewGuid().ToString("N"));
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

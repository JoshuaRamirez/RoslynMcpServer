using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="IntroduceParameterOperation"/>
/// (optional column leftover).
/// </summary>
public class IntroduceParameterOperationTests
{
    private const string MultiDeclaratorSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Run()
            {
                int a = 1, b = 2;
                return a + b;
            }
        }
        """;

    private const string SameLineSameNameSource = """
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
            public int Run()
            {
                int // split-decl
                    value = 1;
                return value;
            }
        }
        """;

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

    #region Input Validation

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new IntroduceParameterParams
        {
            SourceFile = AbsoluteTestPath(),
            VariableName = "total",
            Line = 5
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = "total",
                Line = 5,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = "total",
                Line = 5,
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySourceFile_WithColumn_ThrowsMissingRequiredParam(string sourceFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                SourceFile = sourceFile,
                VariableName = "total",
                Line = 5,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyVariableName_WithColumn_ThrowsMissingRequiredParam(string variableName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = variableName,
                Line = 5,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_WithColumn_ThrowsInvalidLineNumber()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                SourceFile = AbsoluteTestPath(),
                VariableName = "total",
                Line = 0,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    #endregion

    #region FindLocalDeclarator — omitted column preserves start-line + name

    [Fact]
    public void FindLocalDeclarator_OmittedColumn_StartLinePlusNameFirstOrDefault()
    {
        var root = Parse(SameLineSameNameSource);
        var line = FindLine(SameLineSameNameSource, "{ int value = 1; return value; }");

        var omitted = IntroduceParameterOperation.FindLocalDeclarator(root, "value", line, column: null);
        var nullColumn = IntroduceParameterOperation.FindLocalDeclarator(root, "value", line, column: null);

        Assert.NotNull(omitted);
        Assert.NotNull(nullColumn);
        Assert.Equal("1", omitted.Initializer!.Value.ToString());
        Assert.Equal("1", nullColumn.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindLocalDeclarator_OmittedColumn_MultiDeclarator_PicksByName()
    {
        var root = Parse(MultiDeclaratorSource);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var a = IntroduceParameterOperation.FindLocalDeclarator(root, "a", line, column: null);
        var b = IntroduceParameterOperation.FindLocalDeclarator(root, "b", line, column: null);

        Assert.NotNull(a);
        Assert.Equal("a", a.Identifier.Text);
        Assert.Equal("1", a.Initializer!.Value.ToString());
        Assert.NotNull(b);
        Assert.Equal("b", b.Identifier.Text);
        Assert.Equal("2", b.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindLocalDeclarator_OmittedColumn_ContinuationLine_DoesNotUseCoveringSpan()
    {
        var root = Parse(SplitDeclarationSource);
        var typeLine = FindLine(SplitDeclarationSource, "int // split-decl");
        var identifierLine = FindLine(SplitDeclarationSource, "value = 1;");
        Assert.NotEqual(typeLine, identifierLine);

        var onTypeLine = IntroduceParameterOperation.FindLocalDeclarator(
            root, "value", typeLine, column: null);
        var onIdentifierLine = IntroduceParameterOperation.FindLocalDeclarator(
            root, "value", identifierLine, column: null);

        Assert.NotNull(onTypeLine);
        Assert.Equal("value", onTypeLine.Identifier.Text);
        Assert.Null(onIdentifierLine);
    }

    [Fact]
    public void FindLocalDeclarator_OmittedColumn_LineMiss_ReturnsNull()
    {
        var root = Parse(SimpleSource);
        var found = IntroduceParameterOperation.FindLocalDeclarator(root, "total", line: 1, column: null);

        Assert.Null(found);
    }

    #endregion

    #region FindLocalDeclarator — column + line

    [Fact]
    public void FindLocalDeclarator_ColumnOnA_PicksA()
    {
        var root = Parse(MultiDeclaratorSource);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");
        var found = IntroduceParameterOperation.FindLocalDeclarator(
            root, "a", line, ColumnOf(MultiDeclaratorSource, "a = 1"));

        Assert.NotNull(found);
        Assert.Equal("a", found.Identifier.Text);
        Assert.Equal("1", found.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindLocalDeclarator_ColumnOnB_PicksB()
    {
        var root = Parse(MultiDeclaratorSource);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");
        var found = IntroduceParameterOperation.FindLocalDeclarator(
            root, "b", line, ColumnOf(MultiDeclaratorSource, "b = 2"));

        Assert.NotNull(found);
        Assert.Equal("b", found.Identifier.Text);
        Assert.Equal("2", found.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindLocalDeclarator_ColumnOnA_AskingForB_ReturnsNull()
    {
        var root = Parse(MultiDeclaratorSource);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");
        var found = IntroduceParameterOperation.FindLocalDeclarator(
            root, "b", line, ColumnOf(MultiDeclaratorSource, "a = 1"));

        Assert.Null(found);
    }

    [Fact]
    public void FindLocalDeclarator_ColumnOnContinuationIdentifier_PicksDeclarator()
    {
        var root = Parse(SplitDeclarationSource);
        var typeLine = FindLine(SplitDeclarationSource, "int // split-decl");
        var identifierLine = FindLine(SplitDeclarationSource, "value = 1;");
        Assert.NotEqual(typeLine, identifierLine);

        var onIdentifierLine = IntroduceParameterOperation.FindLocalDeclarator(
            root, "value", identifierLine, ColumnOf(SplitDeclarationSource, "value = 1;"));

        Assert.NotNull(onIdentifierLine);
        Assert.Equal("value", onIdentifierLine.Identifier.Text);
    }

    [Fact]
    public void FindLocalDeclarator_AdjacentDeclarators_ExclusiveEndDoesNotStealNext()
    {
        var root = Parse(MultiDeclaratorSource);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");
        var first = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(v => v.Identifier.Text == "a");
        var firstEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondId = ColumnOf(MultiDeclaratorSource, "b = 2");

        var atBAskingForA = IntroduceParameterOperation.FindLocalDeclarator(
            root, "a", line, secondId);
        var atSecondId = IntroduceParameterOperation.FindLocalDeclarator(
            root, "b", line, secondId);
        var atFirstId = IntroduceParameterOperation.FindLocalDeclarator(
            root, "a", line, ColumnOf(MultiDeclaratorSource, "a = 1"));

        Assert.False(IntroduceParameterOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.False(IntroduceParameterOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, secondId));
        Assert.Null(atBAskingForA);
        Assert.NotNull(atSecondId);
        Assert.Equal("2", atSecondId.Initializer!.Value.ToString());
        Assert.NotNull(atFirstId);
        Assert.Equal("1", atFirstId.Initializer!.Value.ToString());
    }

    [Fact]
    public void FindLocalDeclarator_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = Parse(MultiDeclaratorSource);
        var found = IntroduceParameterOperation.FindLocalDeclarator(root, "a", line: 1, column: 1);

        Assert.Null(found);
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

        Assert.True(IntroduceParameterOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(IntroduceParameterOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(IntroduceParameterOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(IntroduceParameterOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region Execute — omitted column / column + line / miss

    [SkippableFact]
    public async Task IntroduceParameter_OmittedColumn_PreservesStartLineNameFirstOrDefault()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineSameNameSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(SameLineSameNameSource, "{ int value = 1; return value; }");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "value");
        Assert.DoesNotContain("int value = 1", updated);
        Assert.Contains("int value = 2", updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_OmittedColumn_MultiDeclarator_PicksByName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "b",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "b");
        Assert.Contains("int a = 1", updated);
        Assert.DoesNotContain("b = 2", updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnOnB_PicksBAmongSameLineMultiDeclarators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "b",
            Line = line,
            Column = ColumnOf(MultiDeclaratorSource, "b = 2")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "b");
        Assert.Contains("int a = 1", updated);
        Assert.DoesNotContain("b = 2", updated);
        Assert.DoesNotContain("int a)", updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnOnA_PicksAAmongSameLineMultiDeclarators()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "a",
            Line = line,
            Column = ColumnOf(MultiDeclaratorSource, "a = 1")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "a");
        Assert.Contains("int b = 2", updated);
        Assert.DoesNotContain("a = 1", updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnOnContinuationLine_PicksDeclarator()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SplitDeclarationSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var identifierLine = FindLine(SplitDeclarationSource, "value = 1;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = identifierLine,
            Column = ColumnOf(SplitDeclarationSource, "value = 1;")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "value");
        Assert.DoesNotContain("value = 1", updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_OmittedColumn_ContinuationLine_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SplitDeclarationSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var identifierLine = FindLine(SplitDeclarationSource, "value = 1;");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "value",
                Line = identifierLine
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnAndLineMiss_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "a",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnOnA_AskingForB_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "b",
                Line = line,
                Column = ColumnOf(MultiDeclaratorSource, "a = 1")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceParameter_LineOnlyMiss_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "total",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceParameter_Column_Preview_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiDeclaratorSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(MultiDeclaratorSource, "int a = 1, b = 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "b",
            Line = line,
            Column = ColumnOf(MultiDeclaratorSource, "b = 2"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Promote 'b' to parameter", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceParameter_ColumnOnSecondSameName_KeepsSelectedDeclarator()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineSameNameSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(SameLineSameNameSource, "{ int value = 1; return value; }");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "value",
            Line = line,
            Column = ColumnOf(SameLineSameNameSource, "value = 2")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "value");
        Assert.Contains("int value = 1", updated);
        Assert.DoesNotContain("int value = 2", updated);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Today's rewrite copies the declaration type node (including leading
    /// indent trivia), so the signature is <c>Run(        int name)</c>
    /// rather than <c>Run(int name)</c>.
    /// </summary>
    private static void AssertPromotedParameter(string updated, string parameterName)
    {
        Assert.Contains($"int {parameterName})", updated, StringComparison.Ordinal);
        Assert.Contains("public int Run(", updated, StringComparison.Ordinal);
    }

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static string NormalizeNewlines(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameterMissing.cs");

    /// <summary>
    /// 1-based line of a single-line snippet. Normalize to <c>\n</c> first
    /// so a lone <c>\r</c> is not treated as a line break.
    /// </summary>
    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        if (snippet.Contains('\n'))
            throw new InvalidOperationException("Snippet must be a single line (CRLF-safe).");

        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    /// <summary>
    /// 1-based column of a single-line snippet. Normalize to <c>\n</c>
    /// first so a lone <c>\r</c> is not treated as a line break.
    /// </summary>
    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        if (snippet.Contains('\n'))
            throw new InvalidOperationException("Snippet must be a single line (CRLF-safe).");

        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameter_" + Guid.NewGuid().ToString("N"));
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

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
/// Operation-level tests for <see cref="ConvertPropertyOperation"/> (UC-CV leftover column).
/// </summary>
public class ConvertPropertyOperationTests
{
    private const string SingleAutoPropertySource = """
        namespace TestApp;

        public class Person
        {
            public int Age { get; set; }
        }
        """;

    private const string SingleFullPropertySource = """
        namespace TestApp;

        public class Person
        {
            private int _age;
            public int Age
            {
                get { return _age; }
                set { _age = value; }
            }
        }
        """;

    private const string SameLineAutoPropertiesSource = """
        namespace TestApp;

        public class Pair
        {
            public int Foo { get; set; } public int Bar { get; set; }
        }
        """;

    private const string ContinuationPropertySource = """
        namespace TestApp;

        public class Split
        {
            public int
            Foo { get; set; }
        }
        """;

    private const string AdjacentPropertiesSource = """
        namespace TestApp;

        public class Adjacent
        {
            public int Foo { get; set; }public int Bar { get; set; }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = "",
                PropertyName = "Age",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                PropertyName = "Age",
                Direction = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NoPropertyNameOrLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                PropertyName = "Age",
                Column = 0,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                PropertyName = "Age",
                Direction = "Bogus"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                PropertyName = "Age",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Existing convert / reject cases

    [SkippableFact]
    public async Task Convert_AutoToFull_RewritesProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            PropertyName = "Age",
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public int Age { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_age", updated, StringComparison.Ordinal);
        Assert.Contains("return _age;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_FullToAuto_RewritesProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleFullPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            PropertyName = "Age",
            Direction = "ToAutoProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("return _age;", updated, StringComparison.Ordinal);
        Assert.Contains("get;", updated, StringComparison.Ordinal);
        Assert.Contains("set;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AlreadyFull_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleFullPropertySource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                SourceFile = workspace.SourcePath,
                PropertyName = "Age",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_AlreadyAuto_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                SourceFile = workspace.SourcePath,
                PropertyName = "Age",
                Direction = "ToAutoProperty"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_MissingProperty_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                SourceFile = workspace.SourcePath,
                PropertyName = "Missing",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    #endregion

    #region P0 omitted column keeps today's propertyName + optional line start-line pick

    [SkippableFact]
    public async Task Convert_OmittedColumn_KeepsPropertyNameAndLinePick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineAutoPropertiesSource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            PropertyName = "Foo",
            Line = FindLine(SameLineAutoPropertiesSource, "public int Foo"),
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public int Foo { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("public int Bar { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_foo", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_OmittedColumn_LineOnly_PicksFirstStartLineProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineAutoPropertiesSource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineAutoPropertiesSource, "public int Foo"),
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public int Foo { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("public int Bar { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_foo", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 column picks the intended property when two share a line

    [SkippableFact]
    public async Task Convert_Column_SelectsSecondPropertyOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineAutoPropertiesSource);
        var operation = new ConvertPropertyOperation(workspace.Context);
        var line = FindLine(SameLineAutoPropertiesSource, "public int Foo");
        var secondColumn = ColumnOf(SameLineAutoPropertiesSource, "Bar");

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Foo { get; set; }", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Bar { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_bar", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_ColumnOnContinuationLine_ConvertsThatProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ContinuationPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ContinuationPropertySource, "Foo { get; set; }"),
            Column = ColumnOf(ContinuationPropertySource, "Foo { get; set; }"),
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("Foo { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_foo", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void FindProperty_OmittedColumn_PicksByNameAndStartLine()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineAutoPropertiesSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineAutoPropertiesSource, "public int Foo");
        var omitted = ConvertPropertyOperation.FindProperty(root, "Foo", line, column: null);
        var byNameAndLine = ConvertPropertyOperation.FindProperty(root, "Bar", line, column: null);

        Assert.NotNull(omitted);
        Assert.NotNull(byNameAndLine);
        Assert.Equal("Foo", omitted.Identifier.Text);
        Assert.Equal("Bar", byNameAndLine.Identifier.Text);
    }

    [Fact]
    public void FindProperty_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineAutoPropertiesSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineAutoPropertiesSource, "public int Foo");
        var first = ConvertPropertyOperation.FindProperty(
            root, null, line, ColumnOf(SameLineAutoPropertiesSource, "Foo"));
        var second = ConvertPropertyOperation.FindProperty(
            root, null, line, ColumnOf(SameLineAutoPropertiesSource, "Bar"));
        var omitted = ConvertPropertyOperation.FindProperty(root, null, line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Equal("Foo", first.Identifier.Text);
        Assert.Equal("Bar", second.Identifier.Text);
        Assert.Equal("Foo", omitted.Identifier.Text);
    }

    [Fact]
    public void FindProperty_ColumnOnContinuationLine_PicksProperty()
    {
        var tree = CSharpSyntaxTree.ParseText(ContinuationPropertySource);
        var root = tree.GetRoot();
        var startLine = FindLine(ContinuationPropertySource, "public int");
        var identifierLine = FindLine(ContinuationPropertySource, "Foo { get; set; }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line pick, so the continuation
        // line does not match without column.
        var omittedOnContinuation = ConvertPropertyOperation.FindProperty(
            root, null, identifierLine, column: null);
        var byColumn = ConvertPropertyOperation.FindProperty(
            root, null, identifierLine, ColumnOf(ContinuationPropertySource, "Foo { get; set; }"));

        Assert.Null(omittedOnContinuation);
        Assert.NotNull(byColumn);
        Assert.Equal("Foo", byColumn.Identifier.Text);
    }

    [Fact]
    public void FindProperty_AdjacentProperties_ExclusiveEndDoesNotStealNext()
    {
        var tree = CSharpSyntaxTree.ParseText(AdjacentPropertiesSource);
        var root = tree.GetRoot();
        var line = FindLine(AdjacentPropertiesSource, "public int Foo");
        var first = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .First(property => property.Identifier.Text == "Foo");
        var firstEndCol = first.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondId = ColumnOf(AdjacentPropertiesSource, "Bar");

        var atExclusiveEnd = ConvertPropertyOperation.FindProperty(root, null, line, firstEndCol);
        var atSecondId = ConvertPropertyOperation.FindProperty(root, null, line, secondId);

        Assert.False(ConvertPropertyOperation.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.True(atExclusiveEnd == null || atExclusiveEnd.Identifier.Text != "Foo");
        Assert.NotNull(atSecondId);
        Assert.Equal("Bar", atSecondId.Identifier.Text);
    }

    [Fact]
    public void FindProperty_ColumnWithoutLine_SameIndentSameName_KeepsFirstMatch()
    {
        const string source = """
            class Outer
            {
                public int Foo { get; set; }
            }
            class Other
            {
                public int Foo { get; set; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var foos = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.Text == "Foo")
            .ToList();
        Assert.Equal(2, foos.Count);
        Assert.Equal(
            foos[0].Identifier.GetLocation().GetLineSpan().StartLinePosition.Character,
            foos[1].Identifier.GetLocation().GetLineSpan().StartLinePosition.Character);

        var found = ConvertPropertyOperation.FindProperty(
            root, "Foo", line: null, ColumnOf(source, "Foo { get; set; }"));

        Assert.NotNull(found);
        Assert.Equal("Foo", found.Identifier.Text);
        Assert.Equal("Outer", ((TypeDeclarationSyntax)found.Parent!).Identifier.Text);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public int A { get; set; }public int B { get; set; } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var property = tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .First(p => p.Identifier.Text == "A");
        var span = property.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ConvertPropertyOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ConvertPropertyOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ConvertPropertyOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ConvertPropertyOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task Convert_Preview_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            PropertyName = "Age",
            Direction = "ToFullProperty",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("ToFullProperty", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("_age", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_Preview_Column_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineAutoPropertiesSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(SameLineAutoPropertiesSource, "public int Foo"),
            Column = ColumnOf(SameLineAutoPropertiesSource, "Bar"),
            Direction = "ToFullProperty",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("ToFullProperty", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("_bar", StringComparison.Ordinal));
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
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertPropertyMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertProperty_" + Guid.NewGuid().ToString("N"));
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

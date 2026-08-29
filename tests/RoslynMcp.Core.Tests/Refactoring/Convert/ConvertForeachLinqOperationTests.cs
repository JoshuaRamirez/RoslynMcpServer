using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="ConvertForeachLinqOperation"/> (UC-CV2 preferQuerySyntax).
/// </summary>
public class ConvertForeachLinqOperationTests
{
    private const string FilterAndProjectSource = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<string> ActiveNames(List<Item> items)
            {
                var results = new List<string>();
                foreach (var item in items)
                {
                    if (item.Active)
                        results.Add(item.Name);
                }
                return results;
            }
        }

        public class Item
        {
            public bool Active { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private const string ProjectSource = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<string> Names(List<Item> items)
            {
                var results = new List<string>();
                foreach (var item in items)
                {
                    results.Add(item.Name);
                }
                return results;
            }
        }

        public class Item
        {
            public string Name { get; set; } = "";
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = "",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = "Types.cs",
                Line = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
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
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Query-syntax form policy (documented leftovers)

    [Theory]
    [InlineData(LinqConversionKind.Project)]
    [InlineData(LinqConversionKind.Filter)]
    [InlineData(LinqConversionKind.FilterAndProject)]
    public void HasQuerySyntaxForm_FilterAndProjectPatterns_IsTrue(LinqConversionKind kind)
    {
        Assert.True(ConvertForeachLinqOperation.HasQuerySyntaxForm(kind));
    }

    [Theory]
    [InlineData(LinqConversionKind.Any)]
    [InlineData(LinqConversionKind.All)]
    [InlineData(LinqConversionKind.FirstOrDefault)]
    [InlineData(LinqConversionKind.Count)]
    [InlineData(LinqConversionKind.Sum)]
    public void HasQuerySyntaxForm_AggregationPatterns_KeepMethodSyntax(LinqConversionKind kind)
    {
        // Any / All / FirstOrDefault / Count-only / Sum have no query-syntax
        // form. preferQuerySyntax must keep method syntax rather than inventing
        // invalid query syntax (from … Any() is not legal).
        Assert.False(ConvertForeachLinqOperation.HasQuerySyntaxForm(kind));
        Assert.Equal(
            "Convert foreach to LINQ method syntax",
            ConvertForeachLinqOperation.DescribeRewrite(kind, preferQuerySyntax: true));
    }

    [Fact]
    public void BuildLinqAssignment_PreferQuerySyntaxOnProject_EmitsQueryNotMethod()
    {
        var conversion = SampleConversion(LinqConversionKind.Project, filter: null, projection: "item.Name");
        var method = ConvertForeachLinqOperation.BuildLinqAssignment(conversion, preferQuerySyntax: false);
        var query = ConvertForeachLinqOperation.BuildLinqAssignment(conversion, preferQuerySyntax: true);

        var methodText = method.NormalizeWhitespace().ToFullString();
        var queryText = query.NormalizeWhitespace().ToFullString();

        Assert.Contains(".Select", methodText, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", methodText, StringComparison.Ordinal);
        Assert.Contains("from ", queryText, StringComparison.Ordinal);
        Assert.Contains("select ", queryText, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", queryText, StringComparison.Ordinal);
        Assert.Contains(".ToList()", queryText, StringComparison.Ordinal);
    }

    #endregion

    #region P0 Default / omitted still emits method syntax

    [SkippableFact]
    public async Task ConvertForeachLinq_DefaultPreferQuerySyntax_EmitsMethodSyntax()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FilterAndProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FilterAndProjectSource, "foreach (var item in items)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
        Assert.Contains(".Where", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntaxFalse_EmitsMethodSyntax()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FilterAndProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FilterAndProjectSource, "foreach (var item in items)"),
            PreferQuerySyntax = false
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains(".Where", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_ProjectPattern_Default_EmitsSelectToList()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ProjectSource, "foreach (var item in items)")
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 preferQuerySyntax true emits query syntax for filter+project

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntaxTrue_FilterAndProject_EmitsQuerySyntax()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FilterAndProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FilterAndProjectSource, "foreach (var item in items)"),
            PreferQuerySyntax = true
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
        Assert.Contains("from ", updated, StringComparison.Ordinal);
        Assert.Contains("where ", updated, StringComparison.Ordinal);
        Assert.Contains("select ", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntaxTrue_Project_EmitsFromSelectToList()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ProjectSource, "foreach (var item in items)"),
            PreferQuerySyntax = true
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("from ", updated, StringComparison.Ordinal);
        Assert.Contains("select ", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("where ", updated, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    #endregion

    #region P0 Preview describes the query-syntax rewrite and writes nothing

    [SkippableFact]
    public async Task ConvertForeachLinq_Preview_PreferQuerySyntaxTrue_DescribesQueryRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FilterAndProjectSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FilterAndProjectSource, "foreach (var item in items)"),
            PreferQuerySyntax = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("query syntax", StringComparison.Ordinal) &&
            change.Description.Contains("from", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("from ", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("where ", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("select ", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_Preview_Default_DescribesMethodRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FilterAndProjectSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FilterAndProjectSource, "foreach (var item in items)"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Where + Select", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains(".Where", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, change =>
            change.Description.Contains("query syntax", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Column disambiguates two foreach statements on one line

    [SkippableFact]
    public async Task ConvertForeachLinq_Column_SelectsSecondForeachOnSameLine()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class SameLine
            {
                public void Collect(List<string> first, List<string> second)
                {
                    var names = new List<string>();
                    var titles = new List<string>();
                    foreach (var a in first) names.Add(a); foreach (var b in second) titles.Add(b);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var line = FindLine(source, "foreach (var a in first)");
        var secondColumn = ColumnOf(source, "foreach (var b in second)");

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("foreach (var a in first) names.Add(a);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var b in second)", updated, StringComparison.Ordinal);
        Assert.Contains("titles", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_Column_SelectsFirstForeachOnSameLine()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class SameLine
            {
                public void Collect(List<string> first, List<string> second)
                {
                    var names = new List<string>();
                    var titles = new List<string>();
                    foreach (var a in first) names.Add(a); foreach (var b in second) titles.Add(b);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var line = FindLine(source, "foreach (var a in first)");
        var firstColumn = ColumnOf(source, "foreach (var a in first)");

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach (var a in first)", updated, StringComparison.Ordinal);
        Assert.Contains("foreach (var b in second) titles.Add(b);", updated, StringComparison.Ordinal);
        Assert.Contains("names", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_OmittedColumn_ConvertsFirstForeachOnTheLine()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class SameLine
            {
                public void Collect(List<string> first, List<string> second)
                {
                    var names = new List<string>();
                    var titles = new List<string>();
                    foreach (var a in first) names.Add(a); foreach (var b in second) titles.Add(b);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "foreach (var a in first)")
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach (var a in first)", updated, StringComparison.Ordinal);
        Assert.Contains("foreach (var b in second) titles.Add(b);", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindForeachStatement_ColumnPicksKeywordCoverage()
    {
        const string source = """
            class C
            {
                void M(int[] xs, int[] ys)
                {
                    var a = new System.Collections.Generic.List<int>();
                    var b = new System.Collections.Generic.List<int>();
                    foreach (var x in xs) a.Add(x); foreach (var y in ys) b.Add(y);
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "foreach (var x in xs)");
        var first = ConvertForeachLinqOperation.FindForeachStatement(root, line, ColumnOf(source, "foreach (var x in xs)"));
        var second = ConvertForeachLinqOperation.FindForeachStatement(root, line, ColumnOf(source, "foreach (var y in ys)"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("x", first.Identifier.Text);
        Assert.Equal("y", second.Identifier.Text);
    }

    #endregion

    #region Helpers

    private static ForeachLinqConversion SampleConversion(
        LinqConversionKind kind,
        string? filter,
        string projection)
    {
        return new ForeachLinqConversion(
            kind,
            SyntaxFactory.IdentifierName("results"),
            SyntaxFactory.IdentifierName("items"),
            "item",
            filter == null ? null : SyntaxFactory.ParseExpression(filter),
            SyntaxFactory.ParseExpression(projection),
            "foreach (var item in items) { }");
    }

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
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinqMissing.cs");

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_" + Guid.NewGuid().ToString("N"));
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

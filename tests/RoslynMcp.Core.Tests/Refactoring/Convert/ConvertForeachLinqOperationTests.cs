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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                AllFiles = false,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = AbsoluteTestPath(),
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Query-syntax form policy (documented leftovers)

    [Fact]
    public void HasQuerySyntaxForm_FilterAndProjectPatterns_IsTrue()
    {
        Assert.True(ConvertForeachLinqOperation.HasQuerySyntaxForm(LinqConversionKind.Project));
        Assert.True(ConvertForeachLinqOperation.HasQuerySyntaxForm(LinqConversionKind.Filter));
        Assert.True(ConvertForeachLinqOperation.HasQuerySyntaxForm(LinqConversionKind.FilterAndProject));
    }

    [Fact]
    public void HasQuerySyntaxForm_AggregationPatterns_KeepMethodSyntax()
    {
        // Any / All / FirstOrDefault / Count-only / Sum have no query-syntax
        // form. preferQuerySyntax must keep method syntax rather than inventing
        // invalid query syntax (from … Any() is not legal).
        LinqConversionKind[] aggregations =
        [
            LinqConversionKind.Any,
            LinqConversionKind.All,
            LinqConversionKind.FirstOrDefault,
            LinqConversionKind.Count,
            LinqConversionKind.Sum
        ];

        foreach (var kind in aggregations)
        {
            Assert.False(ConvertForeachLinqOperation.HasQuerySyntaxForm(kind));
            Assert.Equal(
                "Convert foreach to LINQ method syntax",
                ConvertForeachLinqOperation.DescribeRewrite(kind, preferQuerySyntax: true));
        }
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
        Assert.DoesNotContain("from string", queryText, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", queryText, StringComparison.Ordinal);
        Assert.Contains(".ToList()", queryText, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildLinqAssignment_PreferQuerySyntax_ExplicitElementType_EmitsTypedFrom()
    {
        var conversion = SampleConversion(
            LinqConversionKind.Project,
            filter: null,
            projection: "item.Length",
            elementType: SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)));
        var query = ConvertForeachLinqOperation.BuildLinqAssignment(conversion, preferQuerySyntax: true);
        var queryText = query.NormalizeWhitespace().ToFullString();

        Assert.Contains("from string item in", queryText, StringComparison.Ordinal);
        Assert.Contains("select ", queryText, StringComparison.Ordinal);
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
        Assert.DoesNotContain("from string", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntaxTrue_ExplicitElementType_EmitsTypedFromAndCompiles()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Collector
            {
                public List<int> Lengths(IEnumerable<object> items)
                {
                    var results = new List<int>();
                    foreach (string item in items)
                        results.Add(item.Length);
                    return results;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "foreach (string item in items)"),
            PreferQuerySyntax = true
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
        Assert.Contains("from string item in", updated, StringComparison.Ordinal);
        Assert.Contains("select ", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntaxTrue_VarElementType_StaysUntyped()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Collector
            {
                public List<int> Lengths(IEnumerable<string> items)
                {
                    var results = new List<int>();
                    foreach (var item in items)
                        results.Add(item.Length);
                    return results;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(source, "foreach (var item in items)"),
            PreferQuerySyntax = true
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("from item in", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("from var ", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("from string", updated, StringComparison.Ordinal);
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

    #region AllFiles

    private const string ProjectFileA = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class FileA
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
        """;

    private const string FilterFileB = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class FileB
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
        """;

    private const string NothingFileC = """
        using System.Collections.Generic;

        namespace TestApp;

        public class FileC
        {
            public void Print(List<Item> items)
            {
                foreach (var item in items)
                    System.Console.WriteLine(item.Name);
            }
        }
        """;

    private const string MixedFile = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Mixed
        {
            public List<string> Collect(List<Item> items)
            {
                var results = new List<string>();
                foreach (var item in items)
                    results.Add(item.Name);
                foreach (var item in items)
                    System.Console.WriteLine(item.Name);
                return results;
            }
        }
        """;

    [SkippableFact]
    public async Task Convert_AllFilesFalse_ConvertsOnlySpecifiedForeach()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("FileC.cs", NothingFileC),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            Line = FindLine(ProjectFileA, "foreach (var item in items)")
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("foreach", updatedA, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_OmittedAllFiles_KeepsSingleForeachConvert()
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
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ConvertsEligibleForeachAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("FileC.cs", NothingFileC),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain("foreach", updatedA, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("from ", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach", updatedB, StringComparison.Ordinal);
        Assert.Contains(".Where", updatedB, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithoutSourceFileOrLine_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task Convert_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = false,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesFalse_WithoutLine_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                Line = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ProjectSource);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("FileC.cs", NothingFileC),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("foreach", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains(".Select", StringComparison.Ordinal));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", NothingFileC),
            ("FileC2.cs", NothingFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_MixedEligibleAndIneligible_ConvertsOnlyEligible()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedFile),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in items)", updated, StringComparison.Ordinal);
        Assert.Contains("WriteLine", updated, StringComparison.Ordinal);
        Assert.Empty(ConvertForeachLinqOperation.CollectConvertibleForeach(
            CSharpSyntaxTree.ParseText(updated).GetRoot(),
            preferQuerySyntax: false));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_PreferQuerySyntax_EmitsQueryOnlyForSupportedPatterns()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true,
            PreferQuerySyntax = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("from ", updatedA, StringComparison.Ordinal);
        Assert.Contains("select ", updatedA, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", updatedA, StringComparison.Ordinal);
        Assert.Contains("from ", updatedB, StringComparison.Ordinal);
        Assert.Contains("where ", updatedB, StringComparison.Ordinal);
        Assert.Contains("select ", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain(".Select", updatedB, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void CollectConvertibleForeach_MixedFile_ReturnsOnlyEligible()
    {
        var root = CSharpSyntaxTree.ParseText(MixedFile).GetRoot();
        var collected = ConvertForeachLinqOperation.CollectConvertibleForeach(root, preferQuerySyntax: false);

        Assert.Single(collected);
        Assert.Contains("results.Add", collected[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CollectConvertibleForeach_TwoEligibleOnSameLine_ReturnsBoth()
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

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var collected = ConvertForeachLinqOperation.CollectConvertibleForeach(root, preferQuerySyntax: false);

        Assert.Equal(2, collected.Count);
        Assert.Contains(collected, statement => statement.Identifier.Text == "x");
        Assert.Contains(collected, statement => statement.Identifier.Text == "y");
    }

    #endregion

    #region Helpers

    private const string SharedItemSource = """
        namespace TestApp;

        public class Item
        {
            public bool Active { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private static ForeachLinqConversion SampleConversion(
        LinqConversionKind kind,
        string? filter,
        string projection,
        TypeSyntax? elementType = null)
    {
        return new ForeachLinqConversion(
            kind,
            SyntaxFactory.IdentifierName("results"),
            SyntaxFactory.IdentifierName("items"),
            "item",
            elementType,
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

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                </Project>
                """);

            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePaths.Values.First(),
                    SourcePaths = sourcePaths,
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

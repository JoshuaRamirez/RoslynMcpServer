using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Resolution;
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
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_ColumnWithoutLine_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                SourceFile = AbsoluteTestPath(),
                Column = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_ThrowsInvalidSourcePath()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
            {
                AllFiles = true,
                SourceFile = "FileA.cs"
            }));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_AbsoluteSourceFile_DoesNotRequireExists()
    {
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_ValidateMissing_" + Guid.NewGuid().ToString("N"), "Missing.cs");
        // Path shape is valid; File.Exists is skipped under allFiles (filter throws later).
        ConvertForeachLinqOperation.Validate(new ConvertForeachLinqParams
        {
            AllFiles = true,
            SourceFile = missing
        });
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

    [SkippableFact]
    public async Task ConvertForeachLinq_ExclusiveEndAtPreviousKeyword_ThrowsCannotConvert()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var line = FindLine(source, "foreach (var a in first)");
        var firstKeywordEndCol = FirstForeachKeywordEndColumn(source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = firstKeywordEndCol
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_Preview_ColumnSelectsSecondForeach_DoesNotModifyFile()
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
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var line = FindLine(source, "foreach (var a in first)");
        var secondColumn = ColumnOf(source, "foreach (var b in second)");

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Select", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("titles", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains(".Select", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("names", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
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

    [Fact]
    public void FindForeachStatement_OmittedColumn_PicksFirstKeywordBySpanStart()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineForeachsSource).GetRoot();
        var line = FindLine(SameLineForeachsSource, "foreach (var x in xs)");

        var found = ConvertForeachLinqOperation.FindForeachStatement(root, line, column: null);

        Assert.NotNull(found);
        Assert.Equal("x", found.Identifier.Text);
    }

    [Fact]
    public void FindForeachStatement_OmittedColumn_IndentedKeyword_DoesNotForceColumn1()
    {
        var root = CSharpSyntaxTree.ParseText(IndentedForeachSource).GetRoot();
        var line = FindLine(IndentedForeachSource, "foreach (var item in items)");
        var foreachStmt = root.DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        var startCol = foreachStmt.ForEachKeyword.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        var found = ConvertForeachLinqOperation.FindForeachStatement(root, line, column: null);

        Assert.NotNull(found);
        Assert.Equal("item", found.Identifier.Text);
    }

    [Fact]
    public void FindForeachStatement_AdjacentKeywords_ExclusiveEndDoesNotStealNext()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineForeachsSource).GetRoot();
        var line = FindLine(SameLineForeachsSource, "foreach (var x in xs)");
        var first = root.DescendantNodes().OfType<ForEachStatementSyntax>()
            .First(statement => statement.Identifier.Text == "x");
        var firstKeywordEndCol = first.ForEachKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
        var secondKeyword = ColumnOf(SameLineForeachsSource, "foreach (var y in ys)");

        var atExclusiveEnd = ConvertForeachLinqOperation.FindForeachStatement(root, line, firstKeywordEndCol);
        var atSecond = ConvertForeachLinqOperation.FindForeachStatement(root, line, secondKeyword);

        Assert.False(SpanCoverage.SpanCoversColumn(
            first.ForEachKeyword.GetLocation().GetLineSpan(), line, firstKeywordEndCol));
        Assert.True(atExclusiveEnd == null || atExclusiveEnd.Identifier.Text != "x");
        Assert.NotNull(atSecond);
        Assert.Equal("y", atSecond.Identifier.Text);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineForeachsSource);
        var first = tree.GetRoot().DescendantNodes().OfType<ForEachStatementSyntax>()
            .First(statement => statement.Identifier.Text == "x");
        var span = first.ForEachKeyword.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
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

    #region AllFiles optional sourceFile

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_LimitsWalk()
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
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("foreach", updatedA, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("FileC.cs", NothingFileC),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        // Shared AllFilesDocumentHelpers default path returns ignore-case workspace
        // matches even when File.Exists(flipped) is false (case-sensitive volumes).
        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("foreach", updatedA, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, """
                using System.Collections.Generic;
                using System.Linq;

                namespace TestApp;

                public class Outside
                {
                    public List<string> Names(List<Item> items)
                    {
                        var results = new List<string>();
                        foreach (var item in items)
                            results.Add(item.Name);
                        return results;
                    }
                }
                """);

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new ConvertForeachLinqParams
                {
                    AllFiles = true,
                    SourceFile = outsidePath
                }));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_MissingPath_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("FileB.cs", FilterFileB),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_Missing_" + Guid.NewGuid().ToString("N"), "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", ProjectFileA), ("filea.cs", FilterFileB), ("Item.cs", SharedItemSource)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["filea.cs"]);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("foreach", updatedA, StringComparison.Ordinal);
        Assert.Contains(".Select", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["filea.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", ProjectFileA), ("filea.cs", FilterFileB), ("Item.cs", SharedItemSource)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_RelativePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                SourceFile = "FileA.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_OptionalSourceFile_NonCSharpAbsolutePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", ProjectFileA),
            ("Item.cs", SharedItemSource));
        var operation = new ConvertForeachLinqOperation(workspace.Context);
        var nonCs = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_NonCs_" + Guid.NewGuid().ToString("N") + ".txt");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                SourceFile = nonCs
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    #endregion

    #region Preprocessor directives in the foreach body (#2916)

    private const string ScaleWithIfElseBody = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in values)
                {
        #if DEBUG
                    result.Add(v * 10);
        #else
                    result.Add(v);
        #endif
                }
                return result;
            }
        }
        """;

    private const string PositivesWithConditionalIf = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Positives(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in values)
                {
        #if DEBUG
                    if (v > 0)
        #endif
                        result.Add(v);
                }
                return result;
            }
        }
        """;

    private const string DebugOnlyAddBody = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> DebugOnly(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in values)
                {
        #if DEBUG
                    result.Add(v);
        #endif
                }
                return result;
            }
        }
        """;

    private const string ForeachWrappedInIfDebug = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
        #if DEBUG
                foreach (var v in values)
                {
                    result.Add(v * 10);
                }
        #endif
                return result;
            }
        }
        """;

    [Theory]
    [InlineData(ScaleWithIfElseBody)]
    [InlineData(PositivesWithConditionalIf)]
    [InlineData(DebugOnlyAddBody)]
    public void TryConvert_BodyContainsPreprocessorDirectives_ReturnsFalse(string source)
    {
        var foreachStmt = ParseSingleForeach(source);
        Assert.True(ConvertForeachLinqOperation.StatementContainsPreprocessorDirectives(foreachStmt));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: true, out _, out _));
    }

    [Fact]
    public void TryConvert_ForeachWrappedInIfDebug_StillConverts()
    {
        var foreachStmt = ParseSingleForeach(ForeachWrappedInIfDebug);
        Assert.False(ConvertForeachLinqOperation.StatementContainsPreprocessorDirectives(foreachStmt));
        Assert.True(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
    }

    [SkippableTheory]
    [InlineData(ScaleWithIfElseBody)]
    [InlineData(PositivesWithConditionalIf)]
    [InlineData(DebugOnlyAddBody)]
    public async Task ConvertForeachLinq_BodyContainsPreprocessorDirectives_ThrowsCannotConvert(string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "foreach (var v in values)")
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal("3020", ex.ErrorCode);
        Assert.Contains("preprocessor directives", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_Preview_BodyContainsPreprocessorDirectives_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ScaleWithIfElseBody);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(ScaleWithIfElseBody, "foreach (var v in values)"),
                Preview = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("preprocessor directives", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_PreferQuerySyntax_BodyContainsPreprocessorDirectives_ThrowsCannotConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ScaleWithIfElseBody);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(ScaleWithIfElseBody, "foreach (var v in values)"),
                PreferQuerySyntax = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("preprocessor directives", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_ForeachWrappedInIfDebug_StillConvertsAndKeepsDirectives()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ForeachWrappedInIfDebug);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(ForeachWrappedInIfDebug, "foreach (var v in values)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.Contains("#if DEBUG", updated, StringComparison.Ordinal);
        Assert.Contains("#endif", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    private const string ScaleWithIfElseCollection = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in
        #if DEBUG
                            values
        #else
                            values.Skip(1)
        #endif
                            )
                    result.Add(v * 10);
                return result;
            }
        }
        """;

    private const string ScaleWithIfElseIterationType = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<long> Scale(List<int> values)
            {
                var result = new List<long>();
                foreach (
        #if DEBUG
                    long
        #else
                    int
        #endif
                    v in values)
                {
                    result.Add(v * 10);
                }
                return result;
            }
        }
        """;

    private const string ScaleWithDebugOnlyCollectionSuffix = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in values
        #if DEBUG
                    .Where(x => x > 0)
        #endif
                    )
                {
                    result.Add(v * 10);
                }
                return result;
            }
        }
        """;

    [Theory]
    [InlineData(ScaleWithIfElseCollection)]
    [InlineData(ScaleWithIfElseIterationType)]
    [InlineData(ScaleWithDebugOnlyCollectionSuffix)]
    public void TryConvert_HeaderContainsPreprocessorDirectives_ReturnsFalse(string source)
    {
        var foreachStmt = ParseSingleForeach(source);
        Assert.True(ConvertForeachLinqOperation.StatementContainsPreprocessorDirectives(foreachStmt));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: true, out _, out _));
    }

    [SkippableTheory]
    [InlineData(ScaleWithIfElseCollection, false)]
    [InlineData(ScaleWithIfElseCollection, true)]
    [InlineData(ScaleWithIfElseIterationType, false)]
    [InlineData(ScaleWithDebugOnlyCollectionSuffix, false)]
    public async Task ConvertForeachLinq_HeaderContainsPreprocessorDirectives_ThrowsCannotConvert(string source, bool preferQuerySyntax)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "foreach ("),
                PreferQuerySyntax = preferQuerySyntax
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("preprocessor directives", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsForeachWithHeaderDirectives()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ScaleWithIfElseCollection);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsForeachWithDirectives_ConvertsEligible()
    {
        const string mixed = """
            using System.Collections.Generic;
            using System.Linq;

            namespace TestApp;

            public class Collector
            {
                public List<int> Scale(List<int> values)
                {
                    var result = new List<int>();
                    foreach (var v in values)
                    {
            #if DEBUG
                        result.Add(v * 10);
            #else
                        result.Add(v);
            #endif
                    }
                    return result;
                }

                public List<int> Project(List<int> values)
                {
                    var result = new List<int>();
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                    return result;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(mixed);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#if DEBUG", updated, StringComparison.Ordinal);
        Assert.Contains("#else", updated, StringComparison.Ordinal);
        Assert.Contains("#endif", updated, StringComparison.Ordinal);
        Assert.Contains("result.Add(v);", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        Assert.Equal(1, updated.Split("foreach", StringSplitOptions.None).Length - 1);
        await AssertCompilesAsync(workspace);
    }

    private static ForEachStatementSyntax ParseSingleForeach(string source)
    {
        // Match the default Debug workspace load so a loop wrapped in
        // #if DEBUG is still a ForEachStatementSyntax.
        var options = CSharpParseOptions.Default.WithPreprocessorSymbols("DEBUG");
        var foreachStatements = CSharpSyntaxTree.ParseText(source, options).GetRoot()
            .DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .ToList();
        Assert.Single(foreachStatements);
        return foreachStatements[0];
    }

    #endregion

    #region Directive-guarded accumulator writes before the loop (#3476)

    private const string ReleaseOnlyWriteBeforeLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
        #if !DEBUG
                result.Add(-1);
        #endif
                foreach (var v in values)
                {
                    result.Add(v * 10);
                }
                return result;
            }
        }
        """;

    private const string DebugOnlyWriteBeforeLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
        #if DEBUG
                result.Add(-1);
        #endif
                foreach (var v in values)
                {
                    result.Add(v * 10);
                }
                return result;
            }
        }
        """;

    private const string IfElseWriteBeforeLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
        #if DEBUG
                result.Add(-1);
        #else
                result.Add(-2);
        #endif
                foreach (var v in values)
                    result.Add(v * 10);
                return result;
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopOutVar = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            private static bool Make(out List<int> list)
            {
                list = new List<int>();
                return true;
            }

            public List<int> Scale(List<int> values)
            {
                if (Make(out var result))
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                    return result;
                }
                return new List<int>();
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopPatternVariable = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(object o, List<int> values)
            {
                if (o is List<int> result)
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                    return result;
                }
                return new List<int>();
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopCapturedLocal = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                Action fill = () =>
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                };
                fill();
                return result;
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopSiblingScopeShadow = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                Action fill = () =>
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    { var result = 0; _ = result; }
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                };
                fill();
                return result;
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopSwitchSectionLocal = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values, int k)
            {
                switch (k)
                {
                    case 1:
                        var result = new List<int>();
                        break;
                    case 2:
                        result = new List<int>();
        #if !DEBUG
                        result.Add(-1);
        #endif
                        foreach (var v in values)
                        {
                            result.Add(v * 10);
                        }

                        return result;
                }

                return new List<int>();
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopSwitchArmPattern = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values, object o, List<object> objs)
            {
                var result = new List<int>();
                Action fill = () =>
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    var x = o switch { List<int> result => result.Count, _ => 0 };
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                };
                fill();
                return result;
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopQueryClausePattern = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values, object o, List<object> objs)
            {
                var result = new List<int>();
                Action fill = () =>
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    var q = from x in objs where x is List<int> result select x;
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                };
                fill();
                return result;
            }
        }
        """;

    private const string ReleaseOnlyWriteBeforeLoopDoWhilePattern = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values, object o, List<object> objs)
            {
                var result = new List<int>();
                Action fill = () =>
                {
        #if !DEBUG
                    result.Add(-1);
        #endif
                    do { } while (o is List<int> result && result.Count < 0);
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                };
                fill();
                return result;
            }
        }
        """;

    private const string OutVarNoDirectivesBeforeLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            private static bool Make(out List<int> list)
            {
                list = new List<int>();
                return true;
            }

            public List<int> Scale(List<int> values)
            {
                if (Make(out var result))
                {
                    foreach (var v in values)
                    {
                        result.Add(v * 10);
                    }
                    return result;
                }
                return new List<int>();
            }
        }
        """;

    private const string NoDirectivesBeforeLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Scale(List<int> values)
            {
                var result = new List<int>();
                foreach (var v in values)
                {
                    result.Add(v * 10);
                }
                return result;
            }
        }
        """;

    [Theory]
    [InlineData(ReleaseOnlyWriteBeforeLoop)]
    [InlineData(DebugOnlyWriteBeforeLoop)]
    [InlineData(IfElseWriteBeforeLoop)]
    [InlineData(ReleaseOnlyWriteBeforeLoopOutVar)]
    [InlineData(ReleaseOnlyWriteBeforeLoopPatternVariable)]
    [InlineData(ReleaseOnlyWriteBeforeLoopCapturedLocal)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSiblingScopeShadow)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchSectionLocal)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchArmPattern)]
    [InlineData(ReleaseOnlyWriteBeforeLoopQueryClausePattern)]
    [InlineData(ReleaseOnlyWriteBeforeLoopDoWhilePattern)]
    public void TryConvert_DirectiveGuardedWriteBeforeLoop_ReturnsFalse(string source)
    {
        var foreachStmt = ParseSingleForeach(source);
        Assert.False(ConvertForeachLinqOperation.StatementContainsPreprocessorDirectives(foreachStmt));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: true, out _, out _));
    }

    [Theory]
    [InlineData(NoDirectivesBeforeLoop)]
    [InlineData(ForeachWrappedInIfDebug)]
    [InlineData(OutVarNoDirectivesBeforeLoop)]
    public void TryConvert_NoDirectiveGuardedWriteBeforeLoop_StillConverts(string source)
    {
        var foreachStmt = ParseSingleForeach(source);
        Assert.True(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
    }

    [SkippableTheory]
    [InlineData(ReleaseOnlyWriteBeforeLoop, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoop, true)]
    [InlineData(DebugOnlyWriteBeforeLoop, false)]
    [InlineData(IfElseWriteBeforeLoop, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopOutVar, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopOutVar, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopPatternVariable, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopPatternVariable, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSiblingScopeShadow, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchSectionLocal, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchArmPattern, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopQueryClausePattern, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopDoWhilePattern, false)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSiblingScopeShadow, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchSectionLocal, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchArmPattern, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopQueryClausePattern, true)]
    [InlineData(ReleaseOnlyWriteBeforeLoopDoWhilePattern, true)]
    public async Task ConvertForeachLinq_DirectiveGuardedWriteBeforeLoop_ThrowsCannotConvert(string source, bool preview)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, "foreach (var v in values)"),
                Preview = preview
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(
            "Preprocessor directives (#if / #elif / #else / #endif) guard code between the accumulator's " +
            "declaration and the foreach; the LINQ assignment would discard writes made there in some " +
            "build configuration, so the loop cannot be safely converted.",
            ex.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_NoDirectivesBeforeLoop_StillConverts()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NoDirectivesBeforeLoop);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(NoDirectivesBeforeLoop, "foreach (var v in values)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach", updated, StringComparison.Ordinal);
        Assert.Contains(".Select", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableTheory]
    [InlineData(ReleaseOnlyWriteBeforeLoop)]
    [InlineData(ReleaseOnlyWriteBeforeLoopOutVar)]
    [InlineData(ReleaseOnlyWriteBeforeLoopPatternVariable)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSiblingScopeShadow)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchSectionLocal)]
    [InlineData(ReleaseOnlyWriteBeforeLoopSwitchArmPattern)]
    [InlineData(ReleaseOnlyWriteBeforeLoopQueryClausePattern)]
    [InlineData(ReleaseOnlyWriteBeforeLoopDoWhilePattern)]
    public async Task ConvertForeachLinq_AllFiles_SkipsDirectiveGuardedWriteBeforeLoop(string source)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Accumulator declared outside an enclosing loop (#3402)

    private const string NestedForeachAccumulatorOutsideOuterLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<string> Flatten(string[][] groups)
            {
                var all = new List<string>();
                foreach (var g in groups)
                {
                    foreach (var w in g)
                        if (w.Length > 1)
                            all.Add(w.ToUpper());
                }
                return all;
            }
        }
        """;

    private const string ForeachInsideForAccumulatorOutsideOuterLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Retry(int rounds)
            {
                var seen = new List<int>();
                for (int r = 0; r < rounds; r++)
                {
                    foreach (var n in new[] { r, r + 10 })
                        seen.Add(n);
                }
                return seen;
            }
        }
        """;

    private const string ForeachInsideWhileAccumulatorOutsideOuterLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Drain(Queue<int[]> batches)
            {
                var seen = new List<int>();
                while (batches.Count > 0)
                {
                    var batch = batches.Dequeue();
                    foreach (var n in batch)
                        seen.Add(n * 2);
                }
                return seen;
            }
        }
        """;

    private const string ForeachInsideDoAccumulatorOutsideOuterLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public List<int> Drain(Queue<int[]> batches)
            {
                var seen = new List<int>();
                do
                {
                    if (batches.Count == 0)
                        break;
                    var batch = batches.Dequeue();
                    try
                    {
                        foreach (var n in batch)
                        {
                            if (n > 0)
                                seen.Add(n);
                        }
                    }
                    finally
                    {
                    }
                }
                while (true);
                return seen;
            }
        }
        """;

    private const string ForeachInsideLoopFieldAccumulator = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            private List<int> _seen = new List<int>();

            public void Retry(int rounds)
            {
                for (int r = 0; r < rounds; r++)
                {
                    foreach (var n in new[] { r, r + 10 })
                        _seen.Add(n);
                }
            }
        }
        """;

    private const string ForeachInsideForDeclaredInForInitializer = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public int Retry(int rounds)
            {
                var total = 0;
                for (var seen = new List<int>(); seen.Count < rounds; total++)
                {
                    foreach (var n in new[] { total, total + 10 })
                        seen.Add(n);
                }
                return total;
            }
        }
        """;

    private const string AccumulatorDeclaredInsideOuterLoop = """
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public int Flatten(string[][] groups)
            {
                var total = 0;
                foreach (var g in groups)
                {
                    var all = new List<string>();
                    foreach (var w in g)
                        if (w.Length > 1)
                            all.Add(w.ToUpper());
                    total += all.Count;
                }
                return total;
            }
        }
        """;

    private const string LoopInsideLambdaAccumulatorDeclaredInLambda = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace TestApp;

        public class Collector
        {
            public int Count(string[][] groups)
            {
                var total = 0;
                for (int i = 0; i < groups.Length; i++)
                {
                    Func<string[], List<string>> upper = g =>
                    {
                        var all = new List<string>();
                        foreach (var w in g)
                            all.Add(w.ToUpper());
                        return all;
                    };
                    total += upper(groups[i]).Count;
                }
                return total;
            }
        }
        """;

    private const string EnclosingLoopAccumulatorMessage =
        "The foreach is nested inside an enclosing loop (for / foreach / while / do) and the accumulator is " +
        "declared outside that loop; the LINQ assignment would discard elements added by earlier iterations " +
        "of the enclosing loop, so the loop cannot be safely converted.";

    private static ForEachStatementSyntax ParseForeach(string source, string snippet)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var start = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Snippet not found: {snippet}");
        return root.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .Single(statement => statement.SpanStart == start);
    }

    public static TheoryData<string, string> AccumulatorOutsideEnclosingLoopCases => new()
    {
        { NestedForeachAccumulatorOutsideOuterLoop, "foreach (var w in g)" },
        { ForeachInsideForAccumulatorOutsideOuterLoop, "foreach (var n in new[] { r, r + 10 })" },
        { ForeachInsideWhileAccumulatorOutsideOuterLoop, "foreach (var n in batch)" },
        { ForeachInsideDoAccumulatorOutsideOuterLoop, "foreach (var n in batch)" },
        { ForeachInsideLoopFieldAccumulator, "foreach (var n in new[] { r, r + 10 })" },
        { ForeachInsideForDeclaredInForInitializer, "foreach (var n in new[] { total, total + 10 })" }
    };

    public static TheoryData<string, string, bool, bool> AccumulatorOutsideEnclosingLoopOperationCases
    {
        get
        {
            var data = new TheoryData<string, string, bool, bool>();
            foreach (var row in AccumulatorOutsideEnclosingLoopCases)
            {
                var source = (string)row[0];
                var snippet = (string)row[1];
                data.Add(source, snippet, false, false);
                data.Add(source, snippet, true, false);
                data.Add(source, snippet, false, true);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AccumulatorOutsideEnclosingLoopCases))]
    public void TryConvert_AccumulatorDeclaredOutsideEnclosingLoop_ReturnsFalse(string source, string snippet)
    {
        var foreachStmt = ParseForeach(source, snippet);
        Assert.True(ConvertForeachLinqOperation.AccumulatorDeclaredOutsideEnclosingLoop(foreachStmt));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
        Assert.False(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: true, out _, out _));
    }

    [Theory]
    [InlineData(AccumulatorDeclaredInsideOuterLoop, "foreach (var w in g)")]
    [InlineData(LoopInsideLambdaAccumulatorDeclaredInLambda, "foreach (var w in g)")]
    [InlineData(NoDirectivesBeforeLoop, "foreach (var v in values)")]
    public void TryConvert_AccumulatorNotCarriedAcrossEnclosingLoop_StillConverts(string source, string snippet)
    {
        var foreachStmt = ParseForeach(source, snippet);
        Assert.False(ConvertForeachLinqOperation.AccumulatorDeclaredOutsideEnclosingLoop(foreachStmt));
        Assert.True(ConvertForeachLinqOperation.TryConvert(foreachStmt, preferQuerySyntax: false, out _, out _));
    }

    [SkippableTheory]
    [MemberData(nameof(AccumulatorOutsideEnclosingLoopOperationCases))]
    public async Task ConvertForeachLinq_AccumulatorDeclaredOutsideEnclosingLoop_ThrowsCannotConvert(
        string source,
        string snippet,
        bool preview,
        bool preferQuerySyntax)
    {
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = FindLine(source, snippet),
                Preview = preview,
                PreferQuerySyntax = preferQuerySyntax
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(EnclosingLoopAccumulatorMessage, ex.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [MemberData(nameof(AccumulatorOutsideEnclosingLoopCases))]
    public async Task ConvertForeachLinq_AllFiles_SkipsAccumulatorDeclaredOutsideEnclosingLoop(string source, string snippet)
    {
        Assert.NotEmpty(snippet);
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        foreach (var preferQuerySyntax in new[] { false, true })
        {
            var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
            {
                AllFiles = true,
                PreferQuerySyntax = preferQuerySyntax
            });

            Assert.True(result.Success);
            Assert.Empty(result.Changes!.FilesModified);
            Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        }
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_AccumulatorDeclaredInsideOuterLoop_StillConverts()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccumulatorDeclaredInsideOuterLoop);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(AccumulatorDeclaredInsideOuterLoop, "foreach (var w in g)")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach (var w in g)", updated, StringComparison.Ordinal);
        Assert.Contains("foreach (var g in groups)", updated, StringComparison.Ordinal);
        Assert.Contains(".Where", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task ConvertForeachLinq_AllFiles_AccumulatorDeclaredInsideOuterLoop_StillConverts()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccumulatorDeclaredInsideOuterLoop);
        var operation = new ConvertForeachLinqOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("foreach (var w in g)", updated, StringComparison.Ordinal);
        Assert.Contains(".ToList()", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
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

    private static string FlipPathCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i])
                    ? char.ToLowerInvariant(chars[i])
                    : char.ToUpperInvariant(chars[i]);
                break;
            }
        }

        return new string(chars);
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

    private const string SameLineForeachsSource = """
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

    private const string IndentedForeachSource = """
        class C
        {
            void M(int[] items)
            {
                var results = new System.Collections.Generic.List<int>();
                foreach (var item in items)
                    results.Add(item);
            }
        }
        """;

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private static int FirstForeachKeywordEndColumn(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var first = root.DescendantNodes().OfType<ForEachStatementSyntax>()
            .OrderBy(statement => statement.ForEachKeyword.SpanStart)
            .First();
        return first.ForEachKeyword.GetLocation().GetLineSpan().EndLinePosition.Character + 1;
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

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateWithFilesAsync(explicitCompileItems: false, files: files);

        public static Task<TempWorkspace> CreateWithFilesAsync(
            IReadOnlyList<(string FileName, string Source)> files,
            bool explicitCompileItems) =>
            CreateWithFilesAsync(explicitCompileItems: explicitCompileItems, files: files.ToArray());

        public static async Task<TempWorkspace> CreateWithFilesAsync(
            bool explicitCompileItems,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertForeachLinq_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            // Ordinal keys so case-distinct filenames stay distinct on case-sensitive volumes.
            var sourcePaths = new Dictionary<string, string>(
                explicitCompileItems ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            var compileItems = explicitCompileItems
                ? string.Join(Environment.NewLine, files.Select(f => $"    <Compile Include=\"{f.FileName}\" />"))
                : string.Empty;

            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                {(explicitCompileItems ? "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>" : string.Empty)}
                  </PropertyGroup>
                {(explicitCompileItems ? $"  <ItemGroup>{Environment.NewLine}{compileItems}{Environment.NewLine}  </ItemGroup>" : string.Empty)}
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

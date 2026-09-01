using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = false,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        ConvertPropertyOperation.Validate(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = true,
                Direction = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_InvalidDirection_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = true,
                Direction = "ToExpressionBody"
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithPropertyName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = true,
                PropertyName = "Age",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("propertyName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = true,
                Line = 4,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertPropertyOperation.Validate(new ConvertPropertyParams
            {
                AllFiles = true,
                Column = 1,
                Direction = "ToAutoProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    #region AllFiles

    private const string AutoFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Age { get; set; }
            public string Name { get; set; }
        }
        """;

    private const string AutoFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Count { get; set; }
        }
        """;

    private const string FullFileC = """
        namespace TestApp;

        public class FileC
        {
            private int _done;
            public int Done
            {
                get { return _done; }
                set { _done = value; }
            }
        }
        """;

    private const string FullFileA = """
        namespace TestApp;

        public class FileA
        {
            private int _age;
            public int Age
            {
                get { return _age; }
                set { _age = value; }
            }

            private string _name;
            public string Name
            {
                get { return _name; }
                set { _name = value; }
            }
        }
        """;

    private const string FullFileB = """
        namespace TestApp;

        public class FileB
        {
            private int _count;
            public int Count
            {
                get { return _count; }
                set { _count = value; }
            }
        }
        """;

    private const string AutoFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Done { get; set; }
        }
        """;

    private const string MixedEligibleAndAlreadyFull = """
        namespace TestApp;

        public class Mixed
        {
            public int Eligible { get; set; }

            private int _already;
            public int Already
            {
                get { return _already; }
                set { _already = value; }
            }

            public int ExpressionBodied => 1;
        }
        """;

    private const string InterfaceAbstractAndConcrete = """
        namespace TestApp;

        public interface IHasValue
        {
            int Value { get; set; }
        }

        public abstract class BaseType
        {
            public abstract int Abs { get; set; }
            public int Concrete { get; set; }
        }
        """;

    private const string StaticAndInitializedAutos = """
        namespace TestApp;

        public class Config
        {
            public static int Count { get; set; }
            public int Port { get; set; } = 8080;
        }
        """;

    [SkippableFact]
    public async Task Convert_AllFilesFalse_ConvertsOnlySpecifiedProperty()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", AutoFileA),
            ("FileB.cs", AutoFileB),
            ("FileC.cs", FullFileC));
        var operation = new ConvertPropertyOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            PropertyName = "Age",
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("public int Age { get; set; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("_age", updatedA, StringComparison.Ordinal);
        Assert.Contains("public string Name { get; set; }", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task Convert_OmittedAllFiles_KeepsSingleSiteConvert()
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
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToFullProperty_ConvertsEligiblePropertiesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", AutoFileA),
            ("FileB.cs", AutoFileB),
            ("FileC.cs", FullFileC));
        var operation = new ConvertPropertyOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain("public int Age { get; set; }", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Name { get; set; }", updatedA, StringComparison.Ordinal);
        Assert.Contains("_age", updatedA, StringComparison.Ordinal);
        Assert.Contains("_name", updatedA, StringComparison.Ordinal);
        Assert.Contains("return _age;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return _name;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Count { get; set; }", updatedB, StringComparison.Ordinal);
        Assert.Contains("_count", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToAutoProperty_ConvertsEligiblePropertiesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", FullFileA),
            ("FileB.cs", FullFileB),
            ("FileC.cs", AutoFileC));
        var operation = new ConvertPropertyOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToAutoProperty"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain("return _age;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("return _name;", updatedA, StringComparison.Ordinal);
        Assert.Contains("get;", updatedA, StringComparison.Ordinal);
        Assert.Contains("set;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("return _count;", updatedB, StringComparison.Ordinal);
        Assert.Contains("get;", updatedB, StringComparison.Ordinal);
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
            ("FileA.cs", AutoFileA),
            ("FileB.cs", AutoFileB));
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task Convert_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                AllFiles = false,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithPropertyName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                AllFiles = true,
                PropertyName = "Age",
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("propertyName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                AllFiles = true,
                Line = 4,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleAutoPropertySource);
        var operation = new ConvertPropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertPropertyParams
            {
                AllFiles = true,
                Column = 1,
                Direction = "ToFullProperty"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Convert_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", AutoFileA),
            ("FileB.cs", AutoFileB),
            ("FileC.cs", FullFileC));
        var operation = new ConvertPropertyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty",
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
            c.Description.Contains("ToFullProperty", StringComparison.Ordinal) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("_age", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("_count", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", FullFileC),
            ("FileC2.cs", FullFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new ConvertPropertyOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]));
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_MixedEligibleAndAlreadyConverted_ConvertsOnlyEligible()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndAlreadyFull));
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.DoesNotContain("public int Eligible { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_eligible", updated, StringComparison.Ordinal);
        Assert.Contains("return _already;", updated, StringComparison.Ordinal);
        Assert.Contains("public int ExpressionBodied => 1;", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void CollectEligibleProperties_MixedFile_ReturnsOnlyEligible()
    {
        var root = CSharpSyntaxTree.ParseText(MixedEligibleAndAlreadyFull).GetRoot();
        var collected = ConvertPropertyOperation.CollectEligibleProperties(
            root, ConversionDirection.ToFullProperty);

        Assert.Single(collected);
        Assert.Equal("Eligible", collected[0].Identifier.Text);
    }

    [Fact]
    public void CollectEligibleProperties_SkipsInterfaceAndAbstract()
    {
        var root = CSharpSyntaxTree.ParseText(InterfaceAbstractAndConcrete).GetRoot();
        var collected = ConvertPropertyOperation.CollectEligibleProperties(
            root, ConversionDirection.ToFullProperty);

        Assert.Single(collected);
        Assert.Equal("Concrete", collected[0].Identifier.Text);
    }

    [Fact]
    public void CollectEligibleProperties_ToAutoProperty_SkipsInterfaceAndAbstract()
    {
        const string source = """
            namespace TestApp;

            public interface IHasValue
            {
                int Value { get; set; }
            }

            public abstract class BaseType
            {
                public abstract int Abs { get; set; }
                private int _concrete;
                public int Concrete
                {
                    get { return _concrete; }
                    set { _concrete = value; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var collected = ConvertPropertyOperation.CollectEligibleProperties(
            root, ConversionDirection.ToAutoProperty);

        Assert.Single(collected);
        Assert.Equal("Concrete", collected[0].Identifier.Text);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_SkipsInterfaceAndAbstract_ConvertsConcrete()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Types.cs", InterfaceAbstractAndConcrete));
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int Value { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("public abstract int Abs { get; set; }", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Concrete { get; set; }", updated, StringComparison.Ordinal);
        Assert.Contains("_concrete", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _value", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _abs", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [SkippableFact]
    public async Task Convert_AllFilesTrue_ToFullProperty_CopiesStaticAndInitializer()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Config.cs", StaticAndInitializedAutos));
        var operation = new ConvertPropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertPropertyParams
        {
            AllFiles = true,
            Direction = "ToFullProperty"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static int _count", updated, StringComparison.Ordinal);
        Assert.Contains("private int _port = 8080", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public static int Count { get; set; }", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public int Port { get; set; } = 8080", updated, StringComparison.Ordinal);
        await AssertCompilesAsync(workspace);
    }

    [Fact]
    public void ConvertAllProperties_MultipleAutos_ConvertsEachOnceWithBackingField()
    {
        var root = CSharpSyntaxTree.ParseText(AutoFileA).GetRoot();
        var rewritten = ConvertPropertyOperation.ConvertAllProperties(
            root, ConversionDirection.ToFullProperty, out var convertedCount);

        Assert.Equal(2, convertedCount);
        var text = rewritten.ToFullString();
        Assert.Contains("_age", text, StringComparison.Ordinal);
        Assert.Contains("_name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{ get; set; }", text, StringComparison.Ordinal);

        var second = ConvertPropertyOperation.ConvertAllProperties(
            rewritten, ConversionDirection.ToFullProperty, out var secondCount);
        Assert.Equal(0, secondCount);
        Assert.Equal(2, second.DescendantNodes().OfType<FieldDeclarationSyntax>().Count());
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal(
            "Convert property to ToFullProperty",
            ConvertPropertyOperation.BuildAllFilesDescription(ConversionDirection.ToFullProperty, 1));
        Assert.Equal(
            "Convert 2 properties to ToAutoProperty",
            ConvertPropertyOperation.BuildAllFilesDescription(ConversionDirection.ToAutoProperty, 2));
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

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertProperty_" + Guid.NewGuid().ToString("N"));
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

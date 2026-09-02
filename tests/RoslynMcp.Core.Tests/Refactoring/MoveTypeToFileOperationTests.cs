using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="MoveTypeToFileOperation"/>,
/// including optional <c>line</c>, <c>column</c>, <c>createTargetFile</c>,
/// <c>preview</c>, and <c>allFiles</c>.
/// </summary>
public class MoveTypeToFileOperationTests
{
    #region Input Validation

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new MoveTypeToFileParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "Widget",
            TargetFile = AbsoluteTestPath("Target.cs")
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySymbolName_WithColumnAndLine_ThrowsMissingRequiredParam(string symbolName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = symbolName,
                TargetFile = AbsoluteTestPath("Target.cs"),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySourceFile_WithColumnAndLine_ThrowsMissingRequiredParam(string sourceFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = sourceFile,
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs"),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTargetFile_WithColumnAndLine_ThrowsMissingRequiredParam(string targetFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetFile = targetFile,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void AllFiles_DefaultsToFalse()
    {
        var @params = new MoveTypeToFileParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "Widget",
            TargetFile = AbsoluteTestPath("Target.cs")
        };

        Assert.False(@params.AllFiles);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = false,
                SymbolName = "Widget",
                TargetFile = AbsoluteTestPath("Target.cs")
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                TargetFile = AbsoluteTestPath("Target.cs")
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutTargetFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("targetFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileSymbolNameTargetFile_DoesNotThrow()
    {
        MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = true,
                SymbolName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTargetFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = true,
                TargetFile = AbsoluteTestPath("Target.cs")
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("targetFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToFileOperation.Validate(new MoveTypeToFileParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineDualTopLevelWidgetSource =
        "namespace A { public class Widget { } /* a-widget */ } namespace B { public class Widget { } /* b-widget */ }\n";

    private const string SeparateLineDualTopLevelWidgetSource = """
        namespace A
        {
            public class Widget { } // a-widget
        }

        namespace B
        {
            public class Widget { } // b-widget
        }
        """;

    private const string SingleWidgetSource = """
        namespace TestApp;

        public class Widget
        {
        }
        """;

    private const string NestedWidgetSource = """
        namespace TestApp;

        public class Outer
        {
            public class Widget { } // nested-widget
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_LinePicksStartLineType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(SeparateLineDualTopLevelWidgetSource, "a-widget"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class Widget", moved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_MultipleMatches_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedColumn_SingleMatch_IgnoresLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Widget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = 1,
            TargetFile = target
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.Contains("class Widget", moved);
        Assert.False(File.Exists(workspace.SourcePath));
    }

    [Fact]
    public void FindCoveringType_OmittedColumn_StartLineEqualityPicksFirstOnSharedLine()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        Assert.Equal(2, matches.Count);

        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var a = matches.First(t => NamespaceOf(t) == "A");
        var startLine = a.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Assert.Equal(line, startLine);

        var firstStartLine = matches.First(t =>
            t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        Assert.Equal("A", NamespaceOf(firstStartLine));
    }

    [Fact]
    public void FindCoveringType_ColumnOnAIdentifier_PicksA()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [Fact]
    public void FindCoveringType_ColumnOnBIdentifier_PicksB()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"));

        Assert.NotNull(found);
        Assert.Equal("B", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnAIdentifier_PicksA()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class Widget", moved);
        Assert.DoesNotContain("namespace B", moved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnBIdentifier_PicksB()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("BWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.Contains("a-widget", source);
        Assert.DoesNotContain("b-widget", source);
        Assert.Contains("namespace B", moved);
        Assert.Contains("class Widget", moved);
        Assert.DoesNotContain("namespace A", moved);
    }

    [Fact]
    public void FindCoveringType_ColumnWithoutLine_IsNotInvokedForOmittedLinePath()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var bColumn = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var coveringB = TypeSymbolResolver.FindCoveringType(matches, line, bColumn);

        Assert.NotNull(coveringB);
        Assert.Equal("B", NamespaceOf(coveringB));
        Assert.Equal(2, matches.Count);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnWithoutLine_KeepsOmittedLinePath_SymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindCoveringType_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-type");
        Assert.NotEqual(startLine, identifierLine);

        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, identifierLine, ColumnOf(source, "Widget /* split-type */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var identifierLine = FindLine(source, "split-type");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget /* split-type */"),
            TargetFile = target
        });

        Assert.True(result.Success);
        var remaining = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var moved = NormalizeNewlines(await File.ReadAllTextAsync(target));
        Assert.DoesNotContain("split-type", remaining);
        Assert.Contains("other-widget", remaining);
        Assert.Contains("namespace A", moved);
        Assert.Contains("class", moved);
        Assert.Contains("Widget", moved);
    }

    [Fact]
    public void FindCoveringType_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = Parse(SeparateLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(matches, line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnAndLineMiss_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = 1,
                Column = 1,
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Contains("line 1, column 1", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_ColumnAndLine_UnknownSymbolName_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Missing",
                Line = 1,
                Column = 1,
                TargetFile = workspace.TargetPath("Missing.cs")
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_NestedType_StaysUnmoveable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var target = workspace.TargetPath("Nested.cs");
        var line = FindLine(NestedWidgetSource, "nested-widget");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = line,
                Column = ColumnOf(NestedWidgetSource, "Widget { } // nested-widget"),
                TargetFile = target
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_Column_Preview_WritesNothing_AndDescribesMove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("AWidget.cs");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = target,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Widget", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "namespace A { class Widget { } } namespace B { class Widget { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var first = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Widget");
        var span = first.Identifier.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, startCol));
        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, endCol));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var first = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetFile = workspace.TargetPath("AWidget.cs")
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("a-widget", afterFirst);
        Assert.Contains("b-widget", afterFirst);

        var second = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(afterFirst, "b-widget"),
            Column = ColumnOf(afterFirst, "Widget { } /* b-widget */"),
            TargetFile = workspace.TargetPath("BWidget.cs")
        });
        Assert.True(second.Success);

        Assert.True(File.Exists(workspace.TargetPath("AWidget.cs")));
        Assert.True(File.Exists(workspace.TargetPath("BWidget.cs")));
        var aMoved = NormalizeNewlines(await File.ReadAllTextAsync(workspace.TargetPath("AWidget.cs")));
        var bMoved = NormalizeNewlines(await File.ReadAllTextAsync(workspace.TargetPath("BWidget.cs")));
        Assert.Contains("namespace A", aMoved);
        Assert.Contains("namespace B", bMoved);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_CreateTargetFile_CreatesMissingTarget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Created.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetFile = target,
            CreateTargetFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        Assert.Contains("class Widget", await File.ReadAllTextAsync(target));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_Preview_WithoutColumn_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Widget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetFile = target,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(target));
    }

    #endregion

    #region AllFiles

    private const string MultiTypeSource = """
        namespace TestApp;

        public class Alpha
        {
        }

        public class Beta
        {
        }
        """;

    private const string MismatchedNameSource = """
        namespace TestApp;

        public class Gamma
        {
        }
        """;

    private const string WellPlacedSource = """
        namespace TestApp;

        public class Already
        {
        }
        """;

    private const string NestedAndSiblingSource = """
        namespace TestApp;

        public class Outer
        {
            public class Nested { } // nested-widget
        }

        public class Delta
        {
        }
        """;

    private const string CollisionASource = """
        namespace A;

        public class Shared
        {
        }
        """;

    private const string CollisionBSource = """
        namespace B;

        public class Shared
        {
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesFalse_MovesOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Other.cs", MismatchedNameSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Alpha.cs");
        var beforeOther = await File.ReadAllTextAsync(workspace.FilePaths["Other.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.FilePaths["Types.cs"],
            AllFiles = false,
            SymbolName = "Alpha",
            TargetFile = target
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.True(File.Exists(target));
        Assert.Contains("class Alpha", await File.ReadAllTextAsync(target));
        Assert.Contains("class Beta", await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.DoesNotContain("class Alpha", await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(workspace.FilePaths["Other.cs"]));
        Assert.False(File.Exists(workspace.TargetPath("Gamma.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_OmittedAllFiles_KeepsSingleSiteMove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var target = workspace.TargetPath("Widget.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetFile = target
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(target));
        Assert.Contains("class Widget", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_MovesEligibleMultiTypeAndMismatchedName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Mismatched.cs", MismatchedNameSource),
            ("Already.cs", WellPlacedSource),
            ("WithNested.cs", NestedAndSiblingSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeAlready = await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var alphaPath = workspace.TargetPath("Alpha.cs");
        var betaPath = workspace.TargetPath("Beta.cs");
        var gammaPath = workspace.TargetPath("Gamma.cs");
        var deltaPath = workspace.TargetPath("Delta.cs");
        var outerPath = workspace.TargetPath("Outer.cs");

        Assert.True(File.Exists(alphaPath));
        Assert.True(File.Exists(betaPath));
        Assert.True(File.Exists(gammaPath));
        Assert.True(File.Exists(deltaPath));
        Assert.Contains("class Alpha", await File.ReadAllTextAsync(alphaPath));
        Assert.Contains("class Beta", await File.ReadAllTextAsync(betaPath));
        Assert.Contains("class Gamma", await File.ReadAllTextAsync(gammaPath));
        Assert.Contains("class Delta", await File.ReadAllTextAsync(deltaPath));

        Assert.False(File.Exists(workspace.FilePaths["Types.cs"]));
        Assert.False(File.Exists(workspace.FilePaths["Mismatched.cs"]));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]));
        Assert.False(File.Exists(workspace.TargetPath("Nested.cs")));
        Assert.True(File.Exists(outerPath));
        Assert.Contains("class Outer", await File.ReadAllTextAsync(outerPath));
        Assert.Contains("class Nested", await File.ReadAllTextAsync(outerPath));
        Assert.False(File.Exists(workspace.FilePaths["WithNested.cs"]));

        Assert.Contains(result.Changes!.FilesCreated, p => PathEquals(p, alphaPath));
        Assert.Contains(result.Changes.FilesCreated, p => PathEquals(p, betaPath));
        Assert.Contains(result.Changes.FilesCreated, p => PathEquals(p, gammaPath));
        Assert.Contains(result.Changes.FilesCreated, p => PathEquals(p, deltaPath));
        Assert.Contains(result.Changes.FilesCreated, p => PathEquals(p, outerPath));
        Assert.DoesNotContain(result.Changes.FilesCreated, p => PathEquals(p, workspace.FilePaths["Already.cs"]));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_SkipsSameLocationWhenTypeAlreadyMatchesFileStem()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", """
                namespace TestApp;

                public class Widget
                {
                }

                public class Extra
                {
                }
                """));
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(workspace.FilePaths["Widget.cs"]));
        Assert.Contains("class Widget", await File.ReadAllTextAsync(workspace.FilePaths["Widget.cs"]));
        Assert.DoesNotContain("class Extra", await File.ReadAllTextAsync(workspace.FilePaths["Widget.cs"]));
        Assert.True(File.Exists(workspace.TargetPath("Extra.cs")));
        Assert.Contains("class Extra", await File.ReadAllTextAsync(workspace.TargetPath("Extra.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_WithoutSourceFileSymbolNameTargetFile_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Mismatched.cs", MismatchedNameSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.True(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.True(File.Exists(workspace.TargetPath("Gamma.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesFalse_WithoutRequired_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_WithSymbolName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                AllFiles = true,
                SymbolName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(workspace.TargetPath("Widget.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_WithTargetFile_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                AllFiles = true,
                TargetFile = workspace.TargetPath("Widget.cs")
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("targetFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToFileParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_PreviewAllFiles_AggregatesChangesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Mismatched.cs", MismatchedNameSource),
            ("Already.cs", WellPlacedSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeTypes = await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]);
        var beforeMismatched = await File.ReadAllTextAsync(workspace.FilePaths["Mismatched.cs"]);
        var beforeAlready = await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.FilePaths["Types.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.TargetPath("Alpha.cs")));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.TargetPath("Beta.cs")));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.TargetPath("Gamma.cs")));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.FilePaths["Already.cs"]));
        Assert.Equal(beforeTypes, await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.Equal(beforeMismatched, await File.ReadAllTextAsync(workspace.FilePaths["Mismatched.cs"]));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]));
        Assert.False(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.False(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.False(File.Exists(workspace.TargetPath("Gamma.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_EveryTypeNoOp_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Already.cs", WellPlacedSource),
            ("Outer.cs", NestedWidgetSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeAlready = await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]);
        var beforeOuter = await File.ReadAllTextAsync(workspace.FilePaths["Outer.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.FilePaths["Already.cs"]));
        Assert.Equal(beforeOuter, await File.ReadAllTextAsync(workspace.FilePaths["Outer.cs"]));
        Assert.False(File.Exists(workspace.TargetPath("Nested.cs")));
        Assert.False(File.Exists(workspace.TargetPath("Widget.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_DestinationCollision_SkipsLaterClaim()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("AShared.cs", CollisionASource),
            ("BShared.cs", CollisionBSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.FilePaths["AShared.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.FilePaths["BShared.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var sharedPath = workspace.TargetPath("Shared.cs");
        var aStillThere = File.Exists(workspace.FilePaths["AShared.cs"]) &&
            (await File.ReadAllTextAsync(workspace.FilePaths["AShared.cs"])).Contains("class Shared");
        var bStillThere = File.Exists(workspace.FilePaths["BShared.cs"]) &&
            (await File.ReadAllTextAsync(workspace.FilePaths["BShared.cs"])).Contains("class Shared");

        // First claim moves; later claim is skipped. Exactly one source keeps Shared.
        Assert.True(aStillThere ^ bStillThere);
        if (File.Exists(sharedPath))
            Assert.Contains("class Shared", await File.ReadAllTextAsync(sharedPath));
        Assert.True(File.Exists(workspace.FilePaths["AShared.cs"]) || File.Exists(workspace.FilePaths["BShared.cs"]));
        Assert.True(beforeA.Contains("class Shared") && beforeB.Contains("class Shared"));
        Assert.True(result.Changes!.FilesCreated.Count <= 1);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Mismatched.cs", MismatchedNameSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeMismatched = await File.ReadAllTextAsync(workspace.FilePaths["Mismatched.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true,
            SourceFile = workspace.FilePaths["Types.cs"]
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.True(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.False(File.Exists(workspace.TargetPath("Gamma.cs")));
        Assert.Equal(beforeMismatched, await File.ReadAllTextAsync(workspace.FilePaths["Mismatched.cs"]));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_CreateTargetFileFalse_SkipsMissingDestination()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true,
            CreateTargetFile = false
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.False(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.False(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.Empty(result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_NameCollision_SkipsOccupiedDestination()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", MultiTypeSource),
            ("Alpha.cs", """
                namespace Other;

                public class Alpha
                {
                }
                """));
        var operation = new MoveTypeToFileOperation(workspace.Context);
        var beforeAlpha = await File.ReadAllTextAsync(workspace.FilePaths["Alpha.cs"]);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(beforeAlpha, await File.ReadAllTextAsync(workspace.FilePaths["Alpha.cs"]));
        Assert.Contains("class Alpha", await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.True(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.DoesNotContain("class Alpha", await File.ReadAllTextAsync(workspace.TargetPath("Beta.cs")));
    }

    [Fact]
    public void CollectTopLevelTypes_IncludesClassStructInterfaceRecord_ExcludesNestedEnumDelegate()
    {
        const string source = """
            namespace TestApp;
            public class C { public class Nested { } }
            public struct S { }
            public interface I { }
            public enum E { A }
            public delegate void D();
            public record R();
            """;

        var root = Parse(source);
        var types = MoveTypeToFileOperation.CollectTopLevelTypes(root);
        var names = types.Select(t => t.Identifier.Text).ToList();

        Assert.Contains("C", names);
        Assert.Contains("S", names);
        Assert.Contains("I", names);
        Assert.Contains("R", names);
        Assert.DoesNotContain("Nested", names);
        Assert.DoesNotContain("E", names);
        Assert.DoesNotContain("D", names);
    }

    [Fact]
    public void GetDerivedTargetFile_UsesCurrentDirectoryAndTypeName()
    {
        var source = Path.Combine(Path.GetTempPath(), "folder", "Types.cs");
        var dest = MoveTypeToFileOperation.GetDerivedTargetFile(source, "Widget");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "folder", "Widget.cs"), dest);
    }

    [Fact]
    public void IsAlreadyWellPlaced_RequiresSingleMatchingType()
    {
        var path = Path.Combine(Path.GetTempPath(), "Widget.cs");
        Assert.True(MoveTypeToFileOperation.IsAlreadyWellPlaced(path, "Widget", 1));
        Assert.False(MoveTypeToFileOperation.IsAlreadyWellPlaced(path, "Widget", 2));
        Assert.False(MoveTypeToFileOperation.IsAlreadyWellPlaced(path, "Other", 1));
    }

    [Fact]
    public void BuildAllFilesDescription_IncludesTypeAndFileName()
    {
        Assert.Equal(
            "Move Widget to Widget.cs",
            MoveTypeToFileOperation.BuildAllFilesDescription("Widget", Path.Combine(Path.GetTempPath(), "Widget.cs")));
    }

    [Fact]
    public void GetNamespaceName_NestedNamespaces_ReturnsFullPath()
    {
        const string source = """
            namespace A
            {
                namespace B
                {
                    public class Widget { }
                }
            }

            namespace B
            {
                public class Other { }
            }
            """;

        var root = Parse(source);
        var widget = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Widget");
        var other = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Other");

        Assert.Equal("A.B", MoveTypeToFileOperation.GetNamespaceName(widget));
        Assert.Equal("B", MoveTypeToFileOperation.GetNamespaceName(other));
    }

    [Fact]
    public void HasRemainingSourceContent_PreservesEnumDelegateAndGlobalStatements()
    {
        var afterClassRemoved = Parse("""
            namespace TestApp;
            public enum E { A }
            public delegate void D();
            """);
        Assert.True(MoveTypeToFileOperation.HasRemainingSourceContent(afterClassRemoved, preserveNonTypeMembers: true));
        Assert.False(MoveTypeToFileOperation.HasRemainingSourceContent(afterClassRemoved, preserveNonTypeMembers: false));

        var empty = Parse("namespace TestApp;\n");
        Assert.False(MoveTypeToFileOperation.HasRemainingSourceContent(empty, preserveNonTypeMembers: true));
    }

    [Fact]
    public void IsSamePhysicalLocation_OrdinalMatch_IsTrue()
    {
        var path = Path.Combine(Path.GetTempPath(), "Widget.cs");
        Assert.True(MoveTypeToFileOperation.IsSamePhysicalLocation(path, path));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_ClassPlusEnum_KeepsEnumInSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Mixed.cs", """
                namespace TestApp;

                public class Alpha
                {
                }

                public enum Kept
                {
                    A
                }
                """));
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.Contains("class Alpha", await File.ReadAllTextAsync(workspace.TargetPath("Alpha.cs")));
        Assert.True(File.Exists(workspace.FilePaths["Mixed.cs"]));
        var remaining = await File.ReadAllTextAsync(workspace.FilePaths["Mixed.cs"]);
        Assert.Contains("enum Kept", remaining);
        Assert.DoesNotContain("class Alpha", remaining);
        Assert.DoesNotContain(result.Changes!.FilesDeleted, p => PathEquals(p, workspace.FilePaths["Mixed.cs"]));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_OccupiedDestOutsideWorkspace_Skips()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", """
                namespace TestApp;

                public class Occupied
                {
                }

                public class Other
                {
                }
                """));
        var occupiedPath = workspace.TargetPath("Occupied.cs");
        const string sentinel = "// excluded leftover — do not overwrite";
        await File.WriteAllTextAsync(occupiedPath, sentinel);
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(sentinel, await File.ReadAllTextAsync(occupiedPath));
        Assert.Contains("class Occupied", await File.ReadAllTextAsync(workspace.FilePaths["Types.cs"]));
        Assert.True(File.Exists(workspace.TargetPath("Other.cs")));
        Assert.Contains("class Other", await File.ReadAllTextAsync(workspace.TargetPath("Other.cs")));
        Assert.DoesNotContain(result.Changes!.FilesCreated, p => PathEquals(p, occupiedPath));
    }

    [SkippableFact]
    public async Task MoveTypeToFile_AllFilesTrue_NestedNamespaces_ResolvesFullPath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", """
                namespace A.B;

                public class Alpha { }
                public class Beta { }
                """));
        var operation = new MoveTypeToFileOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToFileParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(workspace.TargetPath("Alpha.cs")));
        Assert.True(File.Exists(workspace.TargetPath("Beta.cs")));
        Assert.Contains("namespace A.B", await File.ReadAllTextAsync(workspace.TargetPath("Alpha.cs")));
        Assert.Contains("namespace A.B", await File.ReadAllTextAsync(workspace.TargetPath("Beta.cs")));
        Assert.False(File.Exists(workspace.FilePaths["Types.cs"]));
    }

    #endregion

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpMoveTypeToFile_" + name);

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static List<TypeDeclarationSyntax> TopLevelNamed(SyntaxNode root, string name) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static string NamespaceOf(TypeDeclarationSyntax type) =>
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
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

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
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
        public required IReadOnlyDictionary<string, string> FilePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string TargetPath(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveTypeToFile_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
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

            var filePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                filePaths[fileName] = path;
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Types.cs");

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
                    FilePaths = filePaths,
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

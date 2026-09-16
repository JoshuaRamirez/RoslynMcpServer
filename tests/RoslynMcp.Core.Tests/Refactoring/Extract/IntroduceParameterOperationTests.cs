using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Resolution;
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

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Compute() => 42;

            public int Run()
            {
                int total = 1 + 2;
                string greeting = "hi";
                int x = Compute();
                return total;
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Capacity()
            {
                int capacity = 10;
                return capacity;
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            public int Field = 1;

            public int Prop => 2;

            public void NoLocals()
            {
            }

            public void UsingLocal()
            {
                using var stream = new System.IO.MemoryStream();
            }

            public void NoInitializer()
            {
                int bare;
                bare = 1;
            }

            public void LocalFunction()
            {
                void Inner()
                {
                    int nested = 1;
                    _ = nested;
                }

                Inner();
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                AllFiles = false,
                VariableName = "total",
                Line = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutVariableName_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameterAllFilesFalse.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                IntroduceParameterOperation.Validate(new IntroduceParameterParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    Line = 5
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutLine_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameterAllFilesFalseLine.cs");
        File.WriteAllText(file, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                IntroduceParameterOperation.Validate(new IntroduceParameterParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    VariableName = "total"
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrVariableName_DoesNotThrow()
    {
        IntroduceParameterOperation.Validate(new IntroduceParameterParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithVariableName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                AllFiles = true,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceParameterOperation.Validate(new IntroduceParameterParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Introduce parameter", IntroduceParameterOperation.BuildAllFilesDescription(1));
        Assert.Equal("Introduce 2 parameters", IntroduceParameterOperation.BuildAllFilesDescription(2));
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

        Assert.False(SpanCoverage.SpanCoversColumn(
            first.GetLocation().GetLineSpan(), line, firstEndCol));
        Assert.False(SpanCoverage.SpanCoversColumn(
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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
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
        Assert.Contains("line 1, column 1", ex.Message, StringComparison.Ordinal);
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
        Assert.Contains($"line {line}, column", ex.Message, StringComparison.Ordinal);
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
        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("column", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    #region allFiles

    [SkippableFact]
    public async Task IntroduceParameter_OmittedAllFiles_KeepsSingleSitePromote()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(SimpleSource, "int total = 1 + 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        AssertPromotedParameter(updated, "total");
        Assert.DoesNotContain("int total = 1 + 2;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_PromotesEligibleLocalsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceParameterOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("int total", updatedA, StringComparison.Ordinal);
        Assert.Contains("string greeting", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Run(", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("int total = 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("string greeting = \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.Contains("int capacity)", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("int capacity = 10;", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_WithoutSourceFileOrVariableName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new IntroduceParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                AllFiles = false,
                VariableName = "total",
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_WithVariableName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                AllFiles = true,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceParameterParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceParameter_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceParameterOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges.Count >= 2);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Introduce", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new IntroduceParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceParameterOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("int total", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Run(", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_SkipsAnonymousFunctionAndOverrideAndRefLocals()
    {
        const string source = """
            namespace TestApp;

            public class Base
            {
                public virtual int Run() => 1;
            }

            public class Derived : Base
            {
                public override int Run()
                {
                    int overridden = 2;
                    return overridden;
                }

                public int WithLambda()
                {
                    // No outer local holding the lambda — only the inner local
                    // must stay put (anonymous-function boundary skip).
                    return new System.Func<int, int>(y =>
                    {
                        var captured = y + 1;
                        return captured;
                    })(1);
                }

                public int WithRef(ref int value)
                {
                    ref int alias = ref value;
                    return alias;
                }

                public int Eligible()
                {
                    int total = 3;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var before = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int overridden = 2;", updated, StringComparison.Ordinal);
        Assert.Contains("var captured = y + 1;", updated, StringComparison.Ordinal);
        Assert.Contains("ref int alias = ref value;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total = 3;", updated, StringComparison.Ordinal);
        Assert.Contains("int total)", updated, StringComparison.Ordinal);
        Assert.Contains("public int Eligible(", updated, StringComparison.Ordinal);
        Assert.NotEqual(before, updated);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_SkipsMethodsWithOptionalOrParams()
    {
        const string source = """
            namespace TestApp;

            public class Sample
            {
                public int Optional(int seed = 1)
                {
                    int total = seed + 1;
                    return total;
                }

                public int Params(params int[] values)
                {
                    int total = values.Length;
                    return total;
                }

                public int Eligible()
                {
                    int capacity = 10;
                    return capacity;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int total = seed + 1;", updated, StringComparison.Ordinal);
        Assert.Contains("int total = values.Length;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int capacity = 10;", updated, StringComparison.Ordinal);
        Assert.Contains("int capacity)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceParameter_NamedArgumentCallSites_AddNameColon()
    {
        const string source = """
            namespace TestApp;

            public class Sample
            {
                public int Run(int seed)
                {
                    int total = seed + 1;
                    return total;
                }

                public int Call() => Run(seed: 2);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(source, "int total = seed + 1;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Run(seed: 2,total:seed + 1)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total = seed + 1;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceParameter_MultipleSameFileCallSites_UpdatesAll()
    {
        const string source = """
            namespace TestApp;

            public class Sample
            {
                public int Run()
                {
                    int total = 1 + 2;
                    return total;
                }

                public int First() => Run();
                public int Second() => Run();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceParameterOperation(workspace.Context);
        var line = FindLine(source, "int total = 1 + 2;");

        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "total",
            Line = line
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("First() => Run(1 + 2);", updated, StringComparison.Ordinal);
        Assert.Contains("Second() => Run(1 + 2);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total = 1 + 2;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceParameter_AllFilesTrue_LinkedDocument_CoalescesAndUpdatesSiblingCallers()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
                public static int Compute()
                {
                    int total = 21;
                    return total;
                }
            }
            """;
        const string anchorASource = """
            namespace TestApp;

            public static class AnchorA
            {
            }
            """;
        const string anchorBSource = """
            namespace TestApp;

            public static class AnchorB
            {
                public static int Use() => Shared.Compute();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new IntroduceParameterOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new IntroduceParameterParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AnchorB.cs"]));

        var updatedShared = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.DoesNotContain("int total = 21;", updatedShared, StringComparison.Ordinal);
        Assert.Contains("int total)", updatedShared, StringComparison.Ordinal);

        var updatedAnchorB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]));
        Assert.Contains("Shared.Compute(21)", updatedAnchorB, StringComparison.Ordinal);

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.DoesNotContain("int total = 21;", texts[0], StringComparison.Ordinal);
    }

    #endregion

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

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
    /// 1-based line of a single-line snippet. Normalize CRLF to <c>\n</c>
    /// first so <c>\r\n</c> counts as one line break (a remaining lone
    /// <c>\r</c> is then converted to <c>\n</c>).
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
    /// 1-based column of a single-line snippet. Normalize CRLF to <c>\n</c>
    /// first so <c>\r\n</c> counts as one line break (a remaining lone
    /// <c>\r</c> is then converted to <c>\n</c>).
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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameter_" + Guid.NewGuid().ToString("N"));
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

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
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
                    SourcePath = firstSource!,
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

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceParameterLinked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            var sharedPath = Path.Combine(directory, "Shared.cs");
            var rootProjectPath = Path.Combine(directory, "ProjectA.csproj");
            var referencedProjectPath = Path.Combine(directory, "ProjectB.csproj");
            var anchorAPath = Path.Combine(directory, "AnchorA.cs");
            var anchorBPath = Path.Combine(directory, "AnchorB.cs");
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var projectAGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectBGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await File.WriteAllTextAsync(sharedPath, sharedSource);
            await File.WriteAllTextAsync(anchorAPath, anchorASource);
            await File.WriteAllTextAsync(anchorBPath, anchorBSource);
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{{projectTypeGuid}}") = "ProjectA", "ProjectA.csproj", "{{projectAGuid}}"
                EndProject
                Project("{{projectTypeGuid}}") = "ProjectB", "ProjectB.csproj", "{{projectBGuid}}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{{projectAGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectAGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectAGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectAGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectBGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectBGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            await File.WriteAllTextAsync(rootProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorA.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(referencedProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorB.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Shared.cs"] = sharedPath,
                ["AnchorA.cs"] = anchorAPath,
                ["AnchorB.cs"] = anchorBPath
            };

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                var linkedCount = context.Solution.Projects
                    .SelectMany(proj => proj.Documents)
                    .Count(d => d.FilePath != null &&
                                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(sharedPath), StringComparison.OrdinalIgnoreCase));
                if (linkedCount < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Expected linked document in both projects, found {linkedCount}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = rootProjectPath,
                    SourcePath = sharedPath,
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

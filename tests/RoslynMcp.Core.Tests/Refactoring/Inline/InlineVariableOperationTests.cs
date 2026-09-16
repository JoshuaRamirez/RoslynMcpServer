using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Resolution;
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

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineVariableOperation.Validate(new InlineVariableParams
            {
                AllFiles = false,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutVariableName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineVariableOperation.Validate(new InlineVariableParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrVariableName_DoesNotThrow()
    {
        InlineVariableOperation.Validate(new InlineVariableParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithVariableName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineVariableOperation.Validate(new InlineVariableParams
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
            InlineVariableOperation.Validate(new InlineVariableParams
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
            InlineVariableOperation.Validate(new InlineVariableParams
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
        Assert.Equal("Inline variable", InlineVariableOperation.BuildAllFilesDescription(1));
        Assert.Equal("Inline 2 variables", InlineVariableOperation.BuildAllFilesDescription(2));
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

        Assert.False(SpanCoverage.SpanCoversColumn(
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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
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


    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Run()
            {
                int total = 1 + 2;
                return total;
            }

            public string Hello()
            {
                string greeting = "hi";
                return greeting;
            }

            public int SkipSideEffect()
            {
                int x = Compute();
                return x;
            }

            public int SkipReassign()
            {
                int y = 1;
                y = 2;
                return y;
            }

            private static int Compute() => 3;
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Run()
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
            private int field = 3;

            public int Run()
            {
                int noInit;
                noInit = 1;
                return noInit + field;
            }

            public void UseRef(ref int value)
            {
                int local = 1;
                UseRef(ref local);
            }

            public void UseIn(in int value)
            {
                int local = 1;
                UseIn(in local);
            }

            public string NameOfOnly()
            {
                int local = 1;
                return nameof(local);
            }

            public int IncrementOnly()
            {
                int local = 1;
                local++;
                return local;
            }
        }
        """;

    [SkippableFact]
    public async Task InlineVariable_AllFilesFalse_InlinesOnlySpecifiedVariable()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            VariableName = "total"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("return 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updatedA, StringComparison.Ordinal);
        Assert.Contains("string greeting = \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineVariable_OmittedAllFiles_KeepsSingleSiteInline()
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
        Assert.Contains("return 1 + 2;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_InlinesEligibleLocalsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("return 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("string greeting", updatedA, StringComparison.Ordinal);
        Assert.Contains("int x = Compute();", updatedA, StringComparison.Ordinal);
        Assert.Contains("int y = 1;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return 10;", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("int capacity", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_WithoutSourceFileOrVariableName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                AllFiles = false,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesFalse_WithoutVariableName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_WithVariableName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                AllFiles = true,
                VariableName = "total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("variableName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SimpleSource);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineVariable_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
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
            c.Description.Contains("Inline", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("return 1 + 2;", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("return 10;", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("FileC", "FileC2", StringComparison.Ordinal)
                .Replace("field", "field2", StringComparison.Ordinal)));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
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
    public async Task InlineVariable_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("return 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.Contains("return \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineVariableOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("return 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_SkipsInArgumentRefExpressionIncrementAndNameof()
    {
        const string source = """
            namespace TestApp;

            public class Mixed
            {
                public void UseIn(in int value) { }

                public void InArg()
                {
                    int x = 1;
                    UseIn(in x);
                }

                public int RefAlias()
                {
                    int value = 2;
                    ref int alias = ref value;
                    alias = 3;
                    return value;
                }

                public int Increment()
                {
                    int count = 3;
                    count++;
                    return count;
                }

                public string NameOf()
                {
                    int named = 4;
                    return nameof(named);
                }

                public int Eligible()
                {
                    int total = 5;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("Mixed.cs", source));
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("UseIn(in x);", updated, StringComparison.Ordinal);
        Assert.Contains("int x = 1;", updated, StringComparison.Ordinal);
        Assert.Contains("ref int alias = ref value;", updated, StringComparison.Ordinal);
        Assert.Contains("int value = 2;", updated, StringComparison.Ordinal);
        Assert.Contains("count++;", updated, StringComparison.Ordinal);
        Assert.Contains("int count = 3;", updated, StringComparison.Ordinal);
        Assert.Contains("nameof(named)", updated, StringComparison.Ordinal);
        Assert.Contains("int named = 4;", updated, StringComparison.Ordinal);
        Assert.Contains("return 5;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updated, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_InArgument_ThrowsUsedInRefContext()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public void UseIn(in int value) { }

                public void Run()
                {
                    int x = 1;
                    UseIn(in x);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "x"
            }));

        Assert.Equal(ErrorCodes.UsedInRefContext, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_RefExpression_ThrowsUsedInRefContext()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public int Run()
                {
                    int value = 1;
                    ref int alias = ref value;
                    alias = 2;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "value"
            }));

        Assert.Equal(ErrorCodes.UsedInRefContext, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_PostIncrement_ThrowsMultipleAssignments()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public int Run()
                {
                    int count = 1;
                    count++;
                    return count;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "count"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_Nameof_ThrowsInvalidSelection()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public string Run()
                {
                    int named = 1;
                    return nameof(named);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "named"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("nameof", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_SkipsUsingRefLocalAnonymousLambdaAndDeconstruction()
    {
        const string source = """
            namespace TestApp;

            using System;

            public class Mixed
            {
                public void UsingLocal()
                {
                    using var resource = (IDisposable?)null;
                    Consume(resource);
                }

                public int RefLocal(ref int storage)
                {
                    ref int alias = ref storage;
                    return alias;
                }

                public object Anonymous()
                {
                    int x = 1;
                    return new { x };
                }

                public int Lambda()
                {
                    Func<int> f = () => 1;
                    return f();
                }

                public int Deconstruction()
                {
                    int x = 1, y = 0;
                    (x, y) = (2, 3);
                    return x;
                }

                public int Eligible()
                {
                    int total = 9;
                    return total;
                }

                private static void Consume(IDisposable? _) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(("Mixed2.cs", source));
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed2.cs"]));
        Assert.Contains("using var resource", updated, StringComparison.Ordinal);
        Assert.Contains("ref int alias = ref storage;", updated, StringComparison.Ordinal);
        Assert.Contains("return new { x };", updated, StringComparison.Ordinal);
        Assert.Contains("int x = 1;", updated, StringComparison.Ordinal);
        Assert.Contains("return (() => 1)();", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Func<int> f", updated, StringComparison.Ordinal);
        Assert.Contains("(x, y) = (2, 3);", updated, StringComparison.Ordinal);
        Assert.Contains("return 9;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updated, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_UsingDeclaration_Throws()
    {
        const string source = """
            namespace TestApp;

            using System;

            public class C
            {
                public void Run()
                {
                    using var resource = (IDisposable?)null;
                    Consume(resource);
                }

                private static void Consume(IDisposable? _) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "resource"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("using", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_DeconstructionAssignment_ThrowsMultipleAssignments()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public int Run()
                {
                    int x = 1, y = 0;
                    (x, y) = (2, 3);
                    return x;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "x"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_LambdaInvocation_ParenthesizesReplacement()
    {
        const string source = """
            namespace TestApp;

            using System;

            public class C
            {
                public int Run()
                {
                    Func<int> f = () => 1;
                    return f();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "f"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return (() => 1)();", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Func<int> f", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_AddressOf_ThrowsUsedInRefContext()
    {
        const string source = """
            namespace TestApp;

            public unsafe class C
            {
                public int* Run()
                {
                    int x = 1;
                    return &x;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "x"
            }));

        Assert.Equal(ErrorCodes.UsedInRefContext, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_ReturnRefLocal_ThrowsUsedInRefContext()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public ref int Run(ref int storage)
                {
                    ref int alias = ref storage;
                    return ref alias;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "alias"
            }));

        Assert.Equal(ErrorCodes.UsedInRefContext, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_SingleSite_InferredAnonymousMember_ThrowsInvalidSelection()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public object Run()
                {
                    int x = 1;
                    return new { x };
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineVariableOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "x"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
        Assert.Contains("anonymous", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineVariable_AllFilesTrue_LinkedDocument_CoalescesToOnePhysicalWrite()
    {
        const string sharedSource = """
            namespace TestApp;

            public class Shared
            {
                public int Run()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;
        const string anchorSource = """
            namespace TestApp;

            public static class Anchor
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorSource, anchorSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new InlineVariableOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Contains("return 1 + 2;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", updated, StringComparison.Ordinal);

        // Sibling DocumentIds must carry identical text after coalescing.
        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.Contains("return 1 + 2;", texts[0], StringComparison.Ordinal);
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineVariable_" + Guid.NewGuid().ToString("N"));
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
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineVariableLinked_" + Guid.NewGuid().ToString("N"));
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
                    .SelectMany(p => p.Documents)
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

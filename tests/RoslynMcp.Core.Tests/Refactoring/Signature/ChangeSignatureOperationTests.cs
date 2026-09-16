using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Operation-level tests for <see cref="ChangeSignatureOperation"/> (leftover column).
/// </summary>
public class ChangeSignatureOperationTests
{
    private const string SingleMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { }
        }
        """;

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public void Process(int x) { }
            public void Other(int y) { }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public void Process(int x) { }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            public void Process(string name) { }
        }
        """;

    private const string IndentedMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { }
        }
        """;

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(int x) { } public void Process(int x, int y) { }
        }
        """;

    private static IReadOnlyList<ParameterChange> KeepXAddFlag() =>
    [
        new() { OriginalName = "x", Name = "x" },
        new() { Name = "flag", Type = "bool" }
    ];

    private static IReadOnlyList<ParameterChange> KeepXYAddFlag() =>
    [
        new() { OriginalName = "x", Name = "x" },
        new() { OriginalName = "y", Name = "y" },
        new() { Name = "flag", Type = "bool" }
    ];

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Parameters = KeepXAddFlag(),
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = false,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        ChangeSignatureOperation.Validate(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                SourceFile = "Worker.cs",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                SourceFile = "/tmp/Worker.txt",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        ChangeSignatureOperation.Validate(new ChangeSignatureParams
        {
            AllFiles = true,
            SourceFile = AbsoluteTestPath(),
            Parameters = KeepXAddFlag()
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                Line = 1,
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeSignatureOperation.Validate(new ChangeSignatureParams
            {
                AllFiles = true,
                Column = 1,
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Change signature", ChangeSignatureOperation.BuildAllFilesDescription(1));
        Assert.Equal("Change 2 signatures", ChangeSignatureOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region P0 omitted column keeps today's MethodName + optional Line pick

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_ChangesTheNamedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_IndentedMethodStillChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndentedMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_SameLineOverloads_ChangesFirstStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_OmittedColumn_MultipleMethodsWithoutLine_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 column picks the intended method when two share a line

    [SkippableFact]
    public async Task ChangeSignature_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Parameters = KeepXYAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x) { }");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = firstColumn,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "flag"]);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"]);
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y", "flag"]);
    }

    [SkippableFact]
    public async Task ChangeSignature_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "Process(int x)"),
            Column = ColumnOf(source, "Process(int x)"),
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "public void Other"),
            Column = ColumnOf(source, "Process(int x)"),
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Other", ("int", "x")));
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var first = ChangeSignatureOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { }"));
        var second = ChangeSignatureOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y)"));
        var omitted = ChangeSignatureOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Single(first.ParameterList.Parameters);
        Assert.Equal(2, second.ParameterList.Parameters.Count);
        Assert.Single(omitted.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(int x) { }

                public void Process(int x, int y) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(int x) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter when more than one
        // match exists — the split signature does not start on the identifier
        // line. Column still selects it.
        var byStartLineOnly = ChangeSignatureOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = ChangeSignatureOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Single(byColumn.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(int x){}public void Process(int x){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(int x)");

        var atSecondStart = ChangeSignatureOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = ChangeSignatureOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = ChangeSignatureOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = ChangeSignatureOperation.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", atSecondStart.Identifier.Text);
        Assert.Equal("Process", atSecondId.Identifier.Text);
        Assert.Equal("Other", atFirstId.Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public void A(int x){}public void B(int x){} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task ChangeSignature_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(int x) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y)");

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Parameters = KeepXYAddFlag(),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Change signature of 'Process'", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("flag", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Existing change_signature / reject cases

    [SkippableFact]
    public async Task ChangeSignature_MethodNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Missing",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_LineDoesNotMatch_ThrowsMethodNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Line = 1,
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Foo(int x)
                {
                }

                public void Foo(int x, int y)
                {
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                Column = column,
                Parameters =
                [
                    new() { OriginalName = "x", Name = "x" },
                    new() { Name = "flag", Type = "bool" }
                ]
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("bool flag", NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task ChangeSignature_ParameterNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Parameters =
                [
                    new() { OriginalName = "missing", Name = "missing" }
                ]
            }));

        Assert.Equal(ErrorCodes.ParameterNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_DuplicateProjectedParameterName_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                Parameters =
                [
                    new() { OriginalName = "x", Name = "x" },
                    new() { Name = "x", Type = "bool" }
                ]
            }));

        Assert.Equal(ErrorCodes.ParameterAlreadyExists, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeSignature_RemoveThenReaddSameName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters =
            [
                new() { OriginalName = "x", Name = "x", Remove = true },
                new() { Name = "x", Type = "string" }
            ]
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.True(HasParameters(updated, "Process", ("string", "x")));
    }

    #endregion

    #region allFiles

    [SkippableFact]
    public async Task ChangeSignature_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("bool flag", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_AppliesToEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var updatedB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        Assert.True(HasParameters(updatedA, "Process", ("int", "x"), ("bool", "flag")));
        Assert.True(HasParameters(updatedA, "Other", ("int", "y")));
        Assert.False(HasParameters(updatedA, "Other", ("int", "y"), ("bool", "flag")));
        Assert.True(HasParameters(updatedB, "Process", ("int", "x"), ("bool", "flag")));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                AllFiles = false,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                AllFiles = true,
                MethodName = "Process",
                Parameters = KeepXAddFlag()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_DuplicateProjectedParameterName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeSignatureParams
            {
                AllFiles = true,
                Parameters =
                [
                    new() { OriginalName = "x", Name = "x" },
                    new() { Name = "@x", Type = "bool" }
                ]
            }));

        Assert.Equal(ErrorCodes.ParameterAlreadyExists, ex.ErrorCode);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Preview = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges.Count >= 2);
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathsEqual(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Change", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_SkipsWhenTargetWouldCollapseOverloads()
    {
        const string source = """
            namespace TestApp;

            public class FileA
            {
                public void Process(int x) { }
                public void Process(int x, bool flag) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "FileA.cs");
        var operation = new ChangeSignatureOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_SkipsOverrideEqualsRatherThanBreakingContract()
    {
        const string source = """
            namespace TestApp;

            public class FileA
            {
                public override bool Equals(object? obj) => false;
                public void Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "FileA.cs");
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters =
            [
                new ParameterChange { OriginalName = "obj", Name = "obj" },
                new ParameterChange { Name = "flag", Type = "bool" }
            ]
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var normalized = updated.Replace(" ", "", StringComparison.Ordinal);
        Assert.Contains("Equals(object?obj)", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("Equals(object?obj,boolflag)", normalized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_SkipsWhenAddedTypeDoesNotBind()
    {
        const string withUsing = """
            namespace TestApp;
            using System.Threading;

            public class FileA
            {
                public void Process(int x) { }
            }
            """;
        const string withoutUsing = """
            namespace TestApp;

            public class FileB
            {
                public void Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", withUsing),
            ("FileB.cs", withoutUsing));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters =
            [
                new ParameterChange { OriginalName = "x", Name = "x" },
                new ParameterChange { Name = "token", Type = "CancellationToken" }
            ]
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        Assert.Contains("CancellationToken", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_SkipsRefParamsRatherThanStrippingModifiers()
    {
        const string withRef = """
            namespace TestApp;

            public class FileA
            {
                public void Process(ref int x) { }
            }
            """;
        const string plain = """
            namespace TestApp;

            public class FileB
            {
                public void Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", withRef),
            ("FileB.cs", plain));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        Assert.True(HasParameters(updatedB, "Process", ("int", "x"), ("bool", "flag")));
        Assert.DoesNotContain(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_DefaultOnlyChange_UpdatesDefaults()
    {
        const string withDefault = """
            namespace TestApp;

            public class FileA
            {
                public void Process(int x = 1) { }
            }
            """;
        const string withoutDefault = """
            namespace TestApp;

            public class FileB
            {
                public void Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", withDefault),
            ("FileB.cs", withoutDefault));
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters =
            [
                new ParameterChange { OriginalName = "x", Name = "x", DefaultValue = "42" }
            ]
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var updatedB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        Assert.Contains("=42", updatedA.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("=42", updatedB.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("=1", updatedA.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"],
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        Assert.Contains("bool flag", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ChangeSignature_MultipleSameFileCallSites_UpdatesAll()
    {
        const string source = """
            namespace TestApp;

            public class Sample
            {
                public void Process(int x) { }

                public void First() => Process(1);
                public void Second() => Process(2);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.True(HasParameters(updated, "Process", ("int", "x"), ("bool", "flag")));
        var compact = updated.Replace(" ", "", StringComparison.Ordinal);
        Assert.Contains("First()=>Process(1,default/*TODO:flag*/)", compact, StringComparison.Ordinal);
        Assert.Contains("Second()=>Process(2,default/*TODO:flag*/)", compact, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_LinkedDocument_CoalescesAndUpdatesSiblingCallers()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
                public static void Process(int x) { }
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
                public static void Use() => Shared.Process(1);
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathsEqual(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new ChangeSignatureOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["Shared.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["AnchorB.cs"]));

        var updatedShared = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.True(HasParameters(updatedShared, "Process", ("int", "x"), ("bool", "flag")));

        var updatedAnchorB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]));
        Assert.Contains(
            "Shared.Process(1,default/*TODO:flag*/)",
            updatedAnchorB.Replace(" ", "", StringComparison.Ordinal),
            StringComparison.Ordinal);

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.True(HasParameters(texts[0], "Process", ("int", "x"), ("bool", "flag")));
    }

    [SkippableFact]
    public async Task ChangeSignature_RecursiveSelfCall_UpdatesInBodyInvocation()
    {
        const string source = """
            namespace TestApp;

            public class Sample
            {
                public int Process(int x)
                {
                    if (x <= 0)
                        return 0;
                    return Process(x - 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeSignatureOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        var compact = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath))
            .Replace(" ", "", StringComparison.Ordinal);
        Assert.True(HasParameters(
            await File.ReadAllTextAsync(workspace.SourcePath),
            "Process",
            ("int", "x"),
            ("bool", "flag")));
        Assert.Contains("returnProcess(x-1,default/*TODO:flag*/);", compact, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ChangeSignature_AllFilesTrue_SkipsExtensionMethodsRatherThanStrippingThis()
    {
        const string extension = """
            namespace TestApp;

            public static class Ext
            {
                public static void Process(this int x) { }
            }
            """;
        const string plain = """
            namespace TestApp;

            public class FileB
            {
                public void Process(int x) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Ext.cs", extension),
            ("FileB.cs", plain));
        var operation = new ChangeSignatureOperation(workspace.Context);
        var beforeExt = await File.ReadAllTextAsync(workspace.SourcePaths["Ext.cs"]);

        var result = await operation.ExecuteAsync(new ChangeSignatureParams
        {
            AllFiles = true,
            Parameters = KeepXAddFlag()
        });

        Assert.True(result.Success);
        Assert.Equal(beforeExt, await File.ReadAllTextAsync(workspace.SourcePaths["Ext.cs"]));
        var updatedB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        Assert.True(HasParameters(updatedB, "Process", ("int", "x"), ("bool", "flag")));
        Assert.DoesNotContain(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["Ext.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, workspace.SourcePaths["FileB.cs"]));
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static List<MethodDeclarationSyntax> GetMethods(string source, string methodName) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

    private static bool HasParameters(string source, string methodName, params (string Type, string Name)[] expected)
    {
        var methods = GetMethods(source, methodName);
        return methods.Any(method =>
            method.ParameterList.Parameters.Count == expected.Length &&
            method.ParameterList.Parameters
                .Select((p, i) => p.Identifier.Text == expected[i].Name &&
                                  (p.Type?.ToString().Trim() ?? "") == expected[i].Type)
                .All(match => match));
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpChangeSignatureMissing.cs");

    private static int FindLine(string source, string snippet)
    {
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
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeSignature_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeSignatureLinked_" + Guid.NewGuid().ToString("N"));
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="AddNullChecksOperation"/> (leftover column).
/// </summary>
public class AddNullChecksOperationTests
{
    private const string SingleMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }
        }
        """;

    private const string IndentedMethodSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }
        }
        """;

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { } public void Process(string name, string extra) { }
        }
        """;

    private const string TwoOverloadsOnSeparateLinesSource = """
        namespace TestApp;

        public class Worker
        {
            public void Process(string name) { }

            public void Process(string name, string extra) { }
        }
        """;

    private const string SameLineConstructorsSource = """
        namespace TestApp;

        public class Worker
        {
            public Worker(string name) { } public Worker(string name, string extra) { }
        }
        """;

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                AllFiles = false,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        AddNullChecksOperation.Validate(new AddNullChecksParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                AllFiles = true,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            AddNullChecksOperation.Validate(new AddNullChecksParams
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
            AddNullChecksOperation.Validate(new AddNullChecksParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region P0 omitted column keeps today's methodName + optional line start-line pick

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_AddsChecksToTheNamedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_IndentedMethodStillGetsChecks()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndentedMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_SeveralMethodsShareName_SilentFirstMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull(extra)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_LinePicksStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var secondLine = FindLine(TwoOverloadsOnSeparateLinesSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = secondLine
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.DoesNotContain("ThrowIfNull", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedColumn_LineMiss_SilentFirstFallback()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoOverloadsOnSeparateLinesSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = 1
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    #endregion

    #region P0 column picks the intended method when two share a line

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.DoesNotContain("ThrowIfNull", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(string name) { }");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var methods = GetMethods(await File.ReadAllTextAsync(workspace.SourcePath), "Process");
        Assert.Equal(2, methods.Count);
        Assert.Contains("ThrowIfNull(name)", methods[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", methods[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var first = AddNullChecksOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(string name) { }"));
        var second = AddNullChecksOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)"));
        var omitted = AddNullChecksOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Single(((MethodDeclarationSyntax)first).ParameterList.Parameters);
        Assert.Equal(2, ((MethodDeclarationSyntax)second).ParameterList.Parameters.Count);
        Assert.Single(((MethodDeclarationSyntax)omitted).ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void Other(string name){}public void Process(string name){}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void Other");
        var secondStart = ColumnOf(source, "public void Process");
        var secondId = ColumnOf(source, "Process(string name)");

        var atSecondStart = AddNullChecksOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = AddNullChecksOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = AddNullChecksOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other(string name)"));
        var firstAtSecondStart = AddNullChecksOperation.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", ((MethodDeclarationSyntax)atSecondStart).Identifier.Text);
        Assert.Equal("Process", ((MethodDeclarationSyntax)atSecondId).Identifier.Text);
        Assert.Equal("Other", ((MethodDeclarationSyntax)atFirstId).Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { public void A(string name){}public void B(string name){} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "A");
        var span = method.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(AddNullChecksOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(AddNullChecksOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(AddNullChecksOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(AddNullChecksOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    #endregion

    #region P0 column on a continuation-line identifier still picks the method

    [SkippableFact]
    public async Task AddNullChecks_ColumnOnContinuationLine_AddsChecksToThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public void
                Process(string name) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "Process(string name)"),
            Column = ColumnOf(source, "Process(string name)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process(string name) { }

                public void Process(string name, string extra) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void");
        var identifierLine = FindLine(source, "Process(string name) { }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter, then silent
        // First() when line misses. The split signature does not start
        // on the identifier line. Column on the continuation identifier
        // still selects the split method.
        var byStartLineOnly = AddNullChecksOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = AddNullChecksOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(string name) { }"));

        Assert.NotNull(byStartLineOnly);
        Assert.Single(((MethodDeclarationSyntax)byStartLineOnly).ParameterList.Parameters);
        Assert.NotNull(byColumn);
        Assert.Single(((MethodDeclarationSyntax)byColumn).ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_DoesNotRequireStartLine()
    {
        const string source = """
            class C
            {
                public void
                Process(string name) { }

                public void Other(string name) { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var identifierLine = FindLine(source, "Process(string name) { }");

        // Only one Process. Omitted column + identifier line misses the
        // start-line filter and silently First()s the split method.
        // Column still selects it by identifier coverage.
        var omitted = AddNullChecksOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = AddNullChecksOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(string name) { }"));

        Assert.NotNull(omitted);
        Assert.NotNull(byColumn);
        Assert.Single(((MethodDeclarationSyntax)omitted).ParameterList.Parameters);
        Assert.Single(((MethodDeclarationSyntax)byColumn).ParameterList.Parameters);
    }

    #endregion

    #region P0 column on a constructor identifier still picks the constructor

    [SkippableFact]
    public async Task AddNullChecks_Column_SelectsConstructorOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineConstructorsSource);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineConstructorsSource, "public Worker(string name) { }");
        var secondColumn = ColumnOf(SameLineConstructorsSource, "Worker(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Worker",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var ctors = GetConstructors(await File.ReadAllTextAsync(workspace.SourcePath), "Worker");
        Assert.Equal(2, ctors.Count);
        Assert.DoesNotContain("ThrowIfNull", ctors[0].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(name)", ctors[1].Body!.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", ctors[1].Body!.ToFullString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ColumnOnConstructorIdentifier_PicksConstructor()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineConstructorsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineConstructorsSource, "public Worker(string name) { }");
        var first = AddNullChecksOperation.FindMethod(
            root, "Worker", line, ColumnOf(SameLineConstructorsSource, "Worker(string name) { }"));
        var second = AddNullChecksOperation.FindMethod(
            root, "Worker", line, ColumnOf(SameLineConstructorsSource, "Worker(string name, string extra)"));

        Assert.IsType<ConstructorDeclarationSyntax>(first);
        Assert.IsType<ConstructorDeclarationSyntax>(second);
        Assert.Single(((ConstructorDeclarationSyntax)first!).ParameterList.Parameters);
        Assert.Equal(2, ((ConstructorDeclarationSyntax)second!).ParameterList.Parameters.Count);
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task AddNullChecks_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process(string name) { }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(string name, string extra)");

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Add null checks to Process", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("ThrowIfNull", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("extra", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Existing add_null_checks / reject cases

    [SkippableFact]
    public async Task AddNullChecks_MethodNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Missing"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AddNullChecks_NoReferenceParameters_ThrowsNoMembersToGenerate()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.NoMembersToGenerate, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public void Process(string name) { }

            public FileA(string extra) { }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public void Fetch(string path) { }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public abstract class FileC
        {
            public void Already(string name)
            {
                ArgumentNullException.ThrowIfNull(name);
            }

            public void ValueOnly(int count) { }

            public void ExpressionBodied(string name) => System.Console.WriteLine(name);

            public abstract void NoBody(string name);
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public abstract class Mixed
        {
            public void Eligible(string name) { }

            public void AlreadyThrow(string name)
            {
                ArgumentNullException.ThrowIfNull(name);
            }

            public void AlreadyGuard(string extra)
            {
                if (extra is null) throw new ArgumentNullException(nameof(extra));
            }

            public void ValueOnly(int count) { }

            public void ExpressionBodied(string name) => System.Console.WriteLine(name);

            public abstract void NoBody(string name);

            public void Partial(string name, string extra)
            {
                ArgumentNullException.ThrowIfNull(name);
            }
        }
        """;

    [SkippableFact]
    public async Task AddNullChecks_AllFilesFalse_AddsOnlySpecifiedMethod()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new AddNullChecksOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("ThrowIfNull(name)", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull(extra)", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task AddNullChecks_OmittedAllFiles_KeepsSingleSiteAdd()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("ThrowIfNull(name)", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_AddsChecksAcrossEligibleMethodsAndFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new AddNullChecksOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("ThrowIfNull(name)", updatedA, StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(extra)", updatedA, StringComparison.Ordinal);
        Assert.Contains("ThrowIfNull(path)", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                AllFiles = false,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesFalse_WithoutMethodName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                AllFiles = true,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleMethodSource);
        var operation = new AddNullChecksOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddNullChecksParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AddNullChecks_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new AddNullChecksOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
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
            c.Description.Contains("null check", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("ThrowIfNull(name)", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("ThrowIfNull(path)", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)));
        var operation = new AddNullChecksOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
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
    public async Task AddNullChecks_AllFilesTrue_SkipsAlreadyCheckedNoBodyAndExpressionBodied()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("ThrowIfNull(name)", GetMethodBody(updated, "Eligible"), StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(GetMethodBody(updated, "AlreadyThrow"), "ThrowIfNull(name)"));
        Assert.DoesNotContain("ThrowIfNull(extra)", GetMethodBody(updated, "AlreadyGuard"), StringComparison.Ordinal);
        Assert.Contains("if (extra is null)", GetMethodBody(updated, "AlreadyGuard"), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", GetMethodBody(updated, "ValueOnly"), StringComparison.Ordinal);
        Assert.Contains("=>", GetMethodDeclaration(updated, "ExpressionBodied"), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", GetMethodDeclaration(updated, "ExpressionBodied"), StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", GetMethodDeclaration(updated, "NoBody"), StringComparison.Ordinal);
        var partial = GetMethodBody(updated, "Partial");
        Assert.Equal(1, CountOccurrences(partial, "ThrowIfNull(name)"));
        Assert.Contains("ThrowIfNull(extra)", partial, StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task AddNullChecks_AllFilesTrue_StyleGuard_UsesGuardClauses()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA));
        var operation = new AddNullChecksOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new AddNullChecksParams
        {
            AllFiles = true,
            Style = "guard"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("if (name is null)", updated, StringComparison.Ordinal);
        Assert.Contains("if (extra is null)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowIfNull", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Add null checks to method", AddNullChecksOperation.BuildAllFilesDescription(1));
        Assert.Equal("Add null checks to 2 methods", AddNullChecksOperation.BuildAllFilesDescription(2));
    }

    [Fact]
    public void HasExistingNullCheck_DetectsThrowIfNullAndGuard()
    {
        var throwTree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M(string name)
                {
                    ArgumentNullException.ThrowIfNull(name);
                }
            }
            """);
        var throwBody = throwTree.GetRoot().DescendantNodes().OfType<BlockSyntax>().First();
        Assert.True(RoslynMcp.Core.Refactoring.Utilities.NullCheckGenerator.HasExistingNullCheck(throwBody, "name"));
        Assert.False(RoslynMcp.Core.Refactoring.Utilities.NullCheckGenerator.HasExistingNullCheck(throwBody, "other"));

        var guardTree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void M(string extra)
                {
                    if (extra is null) throw new ArgumentNullException(nameof(extra));
                }
            }
            """);
        var guardBody = guardTree.GetRoot().DescendantNodes().OfType<BlockSyntax>().First();
        Assert.True(RoslynMcp.Core.Refactoring.Utilities.NullCheckGenerator.HasExistingNullCheck(guardBody, "extra"));
    }

    #endregion

    #region Helpers

    private static IReadOnlyList<MethodDeclarationSyntax> GetMethods(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == name)
            .ToList();

    private static IReadOnlyList<ConstructorDeclarationSyntax> GetConstructors(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.Text == name)
            .ToList();

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpAddNullChecksMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string text, string snippet)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(snippet, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += snippet.Length;
        }

        return count;
    }

    private static string GetMethodBody(string source, string methodName)
    {
        var method = GetMethodDeclaration(source, methodName);
        var start = method.IndexOf('{');
        if (start < 0)
            return method;
        var end = method.LastIndexOf('}');
        return method[start..(end + 1)];
    }

    private static string GetMethodDeclaration(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName);
        return method.ToFullString();
    }

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
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAddNullChecks_" + Guid.NewGuid().ToString("N"));
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

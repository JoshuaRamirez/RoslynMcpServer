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
/// Operation-level tests for <see cref="ConvertToAsyncOperation"/> (UC-CV1 column).
/// </summary>
public class ConvertToAsyncOperationTests
{
    private const string WorkerSource = """
        using System;
        using System.Threading.Tasks;

        namespace TestApp;

        public class Worker
        {
            public void Process()
            {
                Task.Delay(1);
            }

            public async Task CallerAsync()
            {
                Process();
            }

            public void CallerSync()
            {
                Process();
            }
        }
        """;

    private const string SameLineOverloadsSource = """
        using System.Threading.Tasks;

        namespace TestApp;

        public class Worker
        {
            public void Process() { Task.Delay(1); } public void Process(int n) { Task.Delay(n); }
        }
        """;

    #region P0 Default / omitted updateCallers

    [SkippableFact]
    public async Task ConvertToAsync_DefaultUpdateCallers_LeavesCallersUnaugmented()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(0, result.CallersUpdated);
        Assert.Null(result.CallersSkipped);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task ProcessAsync()", updated);
        Assert.Contains("await Task.Delay(1)", updated);
        Assert.Contains("ProcessAsync();", updated);
        Assert.DoesNotContain("await ProcessAsync()", updated);

        var callerAsync = GetMethodBody(updated, "CallerAsync");
        Assert.Contains("ProcessAsync();", callerAsync);
        Assert.DoesNotContain("await", callerAsync);

        var callerSync = GetMethodBody(updated, "CallerSync");
        Assert.Contains("ProcessAsync();", callerSync);
        Assert.DoesNotContain("await", callerSync);
    }

    [SkippableFact]
    public async Task ConvertToAsync_UpdateCallersFalse_LeavesCallersUnaugmented()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = false
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.CallersUpdated);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("await ProcessAsync()", updated);
        Assert.Contains("ProcessAsync();", updated);
    }

    #endregion

    #region P0 updateCallers true awaits async caller

    [SkippableFact]
    public async Task ConvertToAsync_UpdateCallersTrue_AwaitsAlreadyAsyncCaller()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = true
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.CallersUpdated);
        Assert.NotNull(result.CallersSkipped);
        Assert.Contains(result.CallersSkipped, skipped =>
            skipped.Caller == "CallerSync" &&
            skipped.Reason == ConvertToAsyncOperation.SyncCallerSkipReason);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task ProcessAsync()", updated);
        Assert.Contains("await Task.Delay(1)", updated);

        var callerAsync = GetMethodBody(updated, "CallerAsync");
        Assert.Contains("await ProcessAsync();", callerAsync);

        var callerSync = GetMethodBody(updated, "CallerSync");
        Assert.Contains("ProcessAsync();", callerSync);
        Assert.DoesNotContain("await", callerSync);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task ConvertToAsync_Preview_UpdateCallersTrue_DescribesCallerUpdatesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(1, result.CallersUpdated);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Update 1 caller to await", StringComparison.Ordinal));
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Skip 1 caller that cannot legally await", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertToAsync_Preview_DefaultUpdateCallers_DescribesNoCallerUpdatesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(0, result.CallersUpdated);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Callers will not be updated to await", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Sync caller stays compiling

    [SkippableFact]
    public async Task ConvertToAsync_UpdateCallersTrue_SyncCallerDoesNotEmitAwait()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = true
        });

        Assert.True(result.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var callerSync = GetMethodDeclaration(updated, "CallerSync");
        Assert.DoesNotContain("async", callerSync);
        Assert.DoesNotContain("await", callerSync);
        Assert.Contains("ProcessAsync();", callerSync);

        var tree = CSharpSyntaxTree.ParseText(updated);
        var parseErrors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
        Assert.Empty(parseErrors);

        var syncMethod = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "CallerSync");
        Assert.False(syncMethod.Modifiers.Any(SyntaxKind.AsyncKeyword));
        Assert.Empty(syncMethod.DescendantNodes().OfType<AwaitExpressionSyntax>());
    }

    #endregion

    #region renameToAsync with and without updateCallers

    [SkippableFact]
    public async Task ConvertToAsync_RenameToAsyncFalse_UpdateCallersFalse_KeepsNameAndDoesNotAwaitCallers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            RenameToAsync = false,
            UpdateCallers = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process()", updated);
        Assert.DoesNotContain("ProcessAsync", updated);
        Assert.Contains("Process();", GetMethodBody(updated, "CallerAsync"));
        Assert.DoesNotContain("await Process", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_RenameToAsyncFalse_UpdateCallersTrue_AwaitsWithoutRenaming()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            RenameToAsync = false,
            UpdateCallers = true
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.CallersUpdated);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process()", updated);
        Assert.DoesNotContain("ProcessAsync", updated);
        Assert.Contains("await Process();", GetMethodBody(updated, "CallerAsync"));
        Assert.Contains("Process();", GetMethodBody(updated, "CallerSync"));
        Assert.DoesNotContain("await", GetMethodBody(updated, "CallerSync"));
    }

    [SkippableFact]
    public async Task ConvertToAsync_RenameToAsyncTrue_UpdateCallersFalse_RenamesIdentifiersOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            RenameToAsync = true,
            UpdateCallers = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task ProcessAsync()", updated);
        Assert.Contains("ProcessAsync();", GetMethodBody(updated, "CallerAsync"));
        Assert.Contains("ProcessAsync();", GetMethodBody(updated, "CallerSync"));
        Assert.DoesNotContain("await ProcessAsync()", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_RenameToAsyncTrue_UpdateCallersTrue_RenamesAndAwaitsAsyncCaller()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            RenameToAsync = true,
            UpdateCallers = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("await ProcessAsync();", GetMethodBody(updated, "CallerAsync"));
        Assert.Contains("ProcessAsync();", GetMethodBody(updated, "CallerSync"));
    }

    #endregion

    #region Nested sync self-calls and conditional access

    [SkippableFact]
    public async Task ConvertToAsync_UpdateCallersTrue_NestedSyncLocalFunctionAndLambda_NotAwaited()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    Task.Delay(1);
                    void Nested()
                    {
                        Process();
                    }
                    Action go = () => Process();
                    Nested();
                    go();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = true
        });

        Assert.True(result.Success);
        Assert.NotNull(result.CallersSkipped);
        Assert.Contains(result.CallersSkipped, skipped =>
            skipped.Caller == "Nested" &&
            skipped.Reason == ConvertToAsyncOperation.SyncCallerSkipReason);
        Assert.Contains(result.CallersSkipped, skipped =>
            skipped.Caller == "(lambda)" &&
            skipped.Reason == ConvertToAsyncOperation.SyncCallerSkipReason);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task ProcessAsync()", updated);
        Assert.Contains("await Task.Delay(1)", updated);

        var process = CSharpSyntaxTree.ParseText(updated).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.Text == "ProcessAsync");
        var nested = process.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .Single(local => local.Identifier.Text == "Nested");
        Assert.False(nested.Modifiers.Any(SyntaxKind.AsyncKeyword));
        Assert.Empty(nested.DescendantNodes().OfType<AwaitExpressionSyntax>());
        Assert.Contains("ProcessAsync()", nested.ToString(), StringComparison.Ordinal);

        var lambda = process.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>().Single();
        Assert.False(lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword));
        Assert.Empty(lambda.DescendantNodes().OfType<AwaitExpressionSyntax>());
        Assert.Contains("ProcessAsync()", lambda.ToString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ConvertToAsync_UpdateCallersTrue_ConditionalAccessInAsyncCaller_IsAwaited()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    Task.Delay(1);
                }

                public async Task CallerAsync(Worker? other)
                {
                    other?.Process();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            UpdateCallers = true
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.CallersUpdated);
        Assert.Null(result.CallersSkipped);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var callerAsync = GetMethodBody(updated, "CallerAsync");
        Assert.Contains("await other?.ProcessAsync()", callerAsync);
        Assert.DoesNotContain("other?.Process();", callerAsync);
    }

    #endregion

    #region Input Validation

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToAsyncOperation.Validate(new ConvertToAsyncParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertToAsyncOperation.Validate(new ConvertToAsyncParams
            {
                SourceFile = AbsoluteTestPath(),
                MethodName = "Process",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    #endregion

    #region P0 omitted column converts the same method as today

    [SkippableFact]
    public async Task ConvertToAsync_OmittedColumn_ConvertsTheNamedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WorkerSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process()", updated);
        Assert.Contains("await Task.Delay(1)", updated);
        Assert.Contains("public async Task CallerAsync()", updated);
        Assert.Contains("public void CallerSync()", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_OmittedColumn_SameLineOverloads_ConvertsFirstStartLineMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process() { Task.Delay(1); }");

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process()", updated);
        Assert.Contains("public void Process(int n)", updated);
        Assert.DoesNotContain("async Task Process(int n)", updated);
    }

    #endregion

    #region P0 column picks the intended method when two share a line

    [SkippableFact]
    public async Task ConvertToAsync_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process() { Task.Delay(1); }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int n)");

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void Process() { Task.Delay(1); }", updated);
        Assert.Contains("async Task Process(int n)", updated);
        Assert.DoesNotContain("public void Process(int n)", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ConvertToAsyncOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process() { Task.Delay(1); }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process() { Task.Delay(1); }");

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = firstColumn,
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process()", updated);
        Assert.Contains("public void Process(int n)", updated);
        Assert.DoesNotContain("async Task Process(int n)", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_ColumnOnContinuationLine_ConvertsThatMethod()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Split
            {
                public void
                Process() { Task.Delay(1); }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "Process()"),
            Column = ColumnOf(source, "Process()"),
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task", updated);
        Assert.Contains("await Task.Delay(1)", updated);
    }

    [SkippableFact]
    public async Task ConvertToAsync_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Adjacent
            {
                public void A(){Task.Delay(1);}public void Process(){Task.Delay(2);}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = FindLine(source, "public void A()"),
            Column = ColumnOf(source, "Process()"),
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void A(){Task.Delay(1);}", updated);
        Assert.Contains("async Task Process()", updated);
        Assert.DoesNotContain("async Task A()", updated);
    }

    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public void Process() { Task.Delay(1); }");
        var first = ConvertToAsyncOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process() { Task.Delay(1); }"));
        var second = ConvertToAsyncOperation.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int n)"));
        var omitted = ConvertToAsyncOperation.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(omitted);
        Assert.Empty(first.ParameterList.Parameters);
        Assert.Single(second.ParameterList.Parameters);
        Assert.Empty(omitted.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public void
                Process() { Task.Delay(1); }

                public void Process(int n) { Task.Delay(n); }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public void\n");
        var identifierLine = FindLine(source, "Process() { Task.Delay(1); }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter when more than one
        // match exists — the split signature does not start on the identifier
        // line. Column still selects it.
        var byStartLineOnly = ConvertToAsyncOperation.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = ConvertToAsyncOperation.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process() { Task.Delay(1); }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Empty(byColumn.ParameterList.Parameters);
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public void A(){Task.Delay(1);}public void Process(){Task.Delay(2);}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public void A()");
        var secondStart = ColumnOf(source, "public void Process()");
        var secondId = ColumnOf(source, "Process()");

        var atSecondStart = ConvertToAsyncOperation.FindMethod(root, "Process", line, secondStart);
        var atSecondId = ConvertToAsyncOperation.FindMethod(root, "Process", line, secondId);
        var atFirstId = ConvertToAsyncOperation.FindMethod(root, "A", line, ColumnOf(source, "A()"));
        var firstAtSecondStart = ConvertToAsyncOperation.FindMethod(root, "A", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Process", atSecondStart.Identifier.Text);
        Assert.Equal("Process", atSecondId.Identifier.Text);
        Assert.Equal("A", atFirstId.Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    #endregion

    #region P0 preview describes the rewrite and writes nothing

    [SkippableFact]
    public async Task ConvertToAsync_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToAsyncOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public void Process() { Task.Delay(1); }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int n)");

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            Column = secondColumn,
            RenameToAsync = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Convert 'Process' to async", StringComparison.Ordinal) &&
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("async", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Existing convert-to-async behavior

    [SkippableFact]
    public async Task ConvertToAsync_AlreadyAsync_Throws()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public async Task Process()
                {
                    await Task.Delay(1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToAsyncParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.AlreadyAsync, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertToAsync_NoAwaitableCalls_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    var x = 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ConvertToAsyncOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ConvertToAsyncParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process"
            }));

        Assert.Equal(ErrorCodes.NoAsyncCalls, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ConvertToAsync_LineDisambiguatesOverloads()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    Task.Delay(1);
                }

                public void Process(int n)
                {
                    Task.Delay(n);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ConvertToAsyncOperation(workspace.Context);
        var line = FindLine(source, "public void Process(int n)");

        var result = await operation.ExecuteAsync(new ConvertToAsyncParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            Line = line,
            RenameToAsync = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("async Task Process(int n)", updated);
        Assert.Contains("public void Process()", updated);
    }

    #endregion

    #region Helper unit tests

    [Fact]
    public void DescribeCallerUpdates_False_SaysNoneWillHappen()
    {
        var plan = new ConvertToAsyncOperation.CallerUpdatePlan([], []);
        var text = ConvertToAsyncOperation.DescribeCallerUpdates(updateCallers: false, plan);
        Assert.Equal("Callers will not be updated to await the converted method.", text);
    }

    [Fact]
    public void DescribeCallerUpdates_TrueNoCallers_SaysNoneWillHappen()
    {
        var plan = new ConvertToAsyncOperation.CallerUpdatePlan([], []);
        var text = ConvertToAsyncOperation.DescribeCallerUpdates(updateCallers: true, plan);
        Assert.Equal("No callers will be updated to await the converted method.", text);
    }

    [Fact]
    public void GetContainingInvocation_SimpleCall()
    {
        var invocation = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression("Process()");
        var name = (IdentifierNameSyntax)invocation.Expression;
        Assert.Same(invocation, ConvertToAsyncOperation.GetContainingInvocation(name));
    }

    [Fact]
    public void GetContainingInvocation_MemberAccess()
    {
        var invocation = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression("worker.Process()");
        var member = (MemberAccessExpressionSyntax)invocation.Expression;
        Assert.Same(invocation, ConvertToAsyncOperation.GetContainingInvocation(member.Name));
    }

    [Fact]
    public void GetContainingInvocation_MethodGroup_ReturnsNull()
    {
        var name = SyntaxFactory.IdentifierName("Process");
        Assert.Null(ConvertToAsyncOperation.GetContainingInvocation(name));
    }

    [Fact]
    public void GetContainingInvocation_ConditionalAccess()
    {
        var expression = SyntaxFactory.ParseExpression("worker?.Process()");
        var conditional = Assert.IsType<ConditionalAccessExpressionSyntax>(expression);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(conditional.WhenNotNull);
        var binding = Assert.IsType<MemberBindingExpressionSyntax>(invocation.Expression);

        Assert.Same(invocation, ConvertToAsyncOperation.GetContainingInvocation(binding.Name));
        Assert.Same(conditional, ConvertToAsyncOperation.GetAwaitWrapTarget(invocation));
    }

    [Fact]
    public void WrapWithAwait_PrefixesAwait()
    {
        var invocation = SyntaxFactory.ParseExpression("Process()");
        var wrapped = ConvertToAsyncOperation.WrapWithAwait(invocation);
        Assert.Equal("await Process()", wrapped.ToString());
    }

    [Fact]
    public void GetEnclosingCallable_DetectsAsyncMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                async System.Threading.Tasks.Task CallerAsync()
                {
                    Process();
                }
            }
            """);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var (isAsync, name) = ConvertToAsyncOperation.GetEnclosingCallable(invocation);
        Assert.True(isAsync);
        Assert.Equal("CallerAsync", name);
    }

    [Fact]
    public void GetEnclosingCallable_DetectsSyncMethod()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                void CallerSync()
                {
                    Process();
                }
            }
            """);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var (isAsync, name) = ConvertToAsyncOperation.GetEnclosingCallable(invocation);
        Assert.False(isAsync);
        Assert.Equal("CallerSync", name);
    }

    #endregion

    #region Helpers

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string GetMethodBody(string source, string methodName)
    {
        var method = GetMethodDeclaration(source, methodName);
        var start = method.IndexOf('{');
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

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToAsyncMissing.cs");

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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertToAsync_" + Guid.NewGuid().ToString("N"));
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

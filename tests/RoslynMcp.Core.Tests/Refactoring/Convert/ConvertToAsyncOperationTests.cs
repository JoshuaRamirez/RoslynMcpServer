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
/// Operation-level tests for <see cref="ConvertToAsyncOperation"/> (UC-CV1 updateCallers).
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

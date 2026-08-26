using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Operation-level tests for <see cref="InlineMethodOperation"/>.
/// These execute the real refactoring against a loaded workspace.
/// </summary>
public class InlineMethodOperationTests
{
    #region P0 Happy Path

    [SkippableFact]
    public async Task InlineMethod_SimpleVoidMethod_ReplacesAllCallSitesAndRemovesMethod()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                    Log("two");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(2, result.ReferencesUpdated);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("void Log", text);
        Assert.Contains(@"System.Console.WriteLine(""one"")", text);
        Assert.Contains(@"System.Console.WriteLine(""two"")", text);
        Assert.DoesNotContain(@"Log(""one"")", text);
        Assert.DoesNotContain(@"Log(""two"")", text);
    }

    [SkippableFact]
    public async Task InlineMethod_ReturnValueMethod_SubstitutesExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Add(int a, int b)
                {
                    return a + b;
                }

                public int Run()
                {
                    return Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Add"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("int Add", text);
        Assert.Contains("return 1 + 2;", text);
        Assert.DoesNotContain("Add(1, 2)", text);
    }

    [SkippableFact]
    public async Task InlineMethod_ExpressionBodiedMethod_SubstitutesExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Double(int value) => value * 2;

                public int Run()
                {
                    return Double(4);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Double"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("int Double", text);
        Assert.Contains("return 4 * 2;", text);
    }

    [SkippableFact]
    public async Task InlineMethod_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.AfterSnippet != null && c.AfterSnippet.Contains("WriteLine"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task InlineMethod_RemoveMethodFalse_LeavesMethodInPlace()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            RemoveMethod = false
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log", text);
        Assert.Contains(@"System.Console.WriteLine(""one"")", text);
        Assert.DoesNotContain(@"Log(""one"")", text);
    }

    [SkippableFact]
    public async Task InlineMethod_SingleCallSite_LeavesMethodAndOtherCalls()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                    Log("two");
                }
            }
            """;

        var callSite = FindInvocationLocation(source, @"Log(""one"")");
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            CallSiteLocation = new CallSiteLocation
            {
                File = workspace.SourcePath,
                Line = callSite.Line,
                Column = callSite.Column
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log", text);
        Assert.Contains(@"System.Console.WriteLine(""one"")", text);
        Assert.Contains(@"Log(""two"")", text);
        Assert.DoesNotContain(@"Log(""one"")", text);
    }

    #endregion

    #region P0 Reject

    [SkippableFact]
    public async Task InlineMethod_MethodNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public void Run()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Missing"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_Recursive_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Fact(int n)
                {
                    if (n <= 1) return 1;
                    return n * Fact(n - 1);
                }

                public int Run()
                {
                    return Fact(3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Fact"
            }));

        Assert.Equal(ErrorCodes.MethodIsRecursive, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_Virtual_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public virtual void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.MethodIsVirtual, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_NoBody_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Calculator
            {
                public abstract void Log(string message);

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.MethodHasNoBody, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_NoCallSites_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.NoCallSitesFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_UnbracedIfCall_ReplacesStatementAndRemovesMethod()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                public void Run(bool flag)
                {
                    if (flag)
                        Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("void Log", text);
        Assert.Contains(@"System.Console.WriteLine(""one"")", text);
        Assert.DoesNotContain(@"Log(""one"")", text);
    }

    [SkippableFact]
    public async Task InlineMethod_SideEffectArgumentUsedTwice_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Next() => 1;

                private int Twice(int value) => value + value;

                public int Run()
                {
                    return Twice(Next());
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Twice"
            }));

        Assert.Equal(ErrorCodes.CannotInlineSideEffects, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_ForeignReceiver_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _value = 1;

                private int GetValue() => _value;

                public int Run()
                {
                    var other = new Calculator();
                    return other.GetValue();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "GetValue"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_StatementsBeforeReturnUsedAsExpression_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Get()
                {
                    System.Console.WriteLine("x");
                    return 1;
                }

                public int Run()
                {
                    return Get();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Get"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_MethodGroupReference_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int Convert(int value) => value * 2;

                public int Run()
                {
                    var items = new[] { 1 };
                    var projected = System.Linq.Enumerable.Select(items, Convert);
                    return Convert(3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Convert"
            }));

        Assert.Equal(ErrorCodes.InvalidSelection, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_NestedReturn_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    if (message == null)
                        return;
                    System.Console.WriteLine(message);
                }

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.UnresolvableControlFlow, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task InlineMethod_Overload_RemovesOnlySelectedMethod()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Log(string message)
                {
                    System.Console.WriteLine(message);
                }

                private void Log(int value)
                {
                    System.Console.WriteLine(value);
                }

                public void Run()
                {
                    Log("one");
                }
            }
            """;

        var methodLine = FindMethodIdentifierLine(source, "Log");
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = methodLine
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log(int value)", text);
        Assert.DoesNotContain("void Log(string message)", text);
        Assert.Contains(@"System.Console.WriteLine(""one"")", text);
        Assert.DoesNotContain(@"Log(""one"")", text);
    }

    [SkippableFact]
    public async Task InlineMethod_AwaitParent_ParenthesizesBinaryExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private AwaitableInt _left = new AwaitableInt(1);
                private AwaitableInt _right = new AwaitableInt(2);

                private AwaitableInt Sum() => _left + _right;

                public async System.Threading.Tasks.Task<int> Run()
                {
                    return await Sum();
                }
            }

            public readonly struct AwaitableInt
            {
                public AwaitableInt(int value) => Value = value;
                public int Value { get; }
                public System.Runtime.CompilerServices.TaskAwaiter<int> GetAwaiter() =>
                    System.Threading.Tasks.Task.FromResult(Value).GetAwaiter();
                public static AwaitableInt operator +(AwaitableInt left, AwaitableInt right) =>
                    new AwaitableInt(left.Value + right.Value);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Sum"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("await (_left + _right)", text);
        Assert.DoesNotContain("await _left + _right", text);
    }

    #endregion

    #region Helpers

    private static int FindMethodIdentifierLine(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var method = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(node => node.Identifier.Text == methodName);
        return method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }

    private static (int Line, int Column) FindInvocationLocation(string source, string invocationText)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var invocation = tree.GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(node => node.ToString() == invocationText);
        var span = invocation.GetLocation().GetLineSpan();
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethod_" + Guid.NewGuid().ToString("N"));
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

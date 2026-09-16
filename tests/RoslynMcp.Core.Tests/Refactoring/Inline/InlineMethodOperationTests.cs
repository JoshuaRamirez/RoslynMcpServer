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

    #region Covering-span column

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Calculator
        {
            private void Log(string message) { System.Console.WriteLine(message); } private void Log(int value) { System.Console.WriteLine(value); }

            public void Run()
            {
                Log("one");
                Log(2);
            }
        }
        """;

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethodInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                InlineMethodOperation.Validate(new InlineMethodParams
                {
                    SourceFile = path,
                    MethodName = "Log",
                    Column = 0
                }));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethodNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                InlineMethodOperation.Validate(new InlineMethodParams
                {
                    SourceFile = path,
                    MethodName = "Log",
                    Column = -1
                }));

            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineMethodOperation.Validate(new InlineMethodParams
            {
                AllFiles = false,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutMethodName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethodAllFilesFalse.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                InlineMethodOperation.Validate(new InlineMethodParams
                {
                    AllFiles = false,
                    SourceFile = path
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        InlineMethodOperation.Validate(new InlineMethodParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineMethodOperation.Validate(new InlineMethodParams
            {
                AllFiles = true,
                MethodName = "Log"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineMethodOperation.Validate(new InlineMethodParams
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
            InlineMethodOperation.Validate(new InlineMethodParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithCallSiteLocation_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            InlineMethodOperation.Validate(new InlineMethodParams
            {
                AllFiles = true,
                CallSiteLocation = new CallSiteLocation
                {
                    File = Path.Combine(Path.GetTempPath(), "x.cs"),
                    Line = 1,
                    Column = 1
                }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Inline method", InlineMethodOperation.BuildAllFilesDescription(1));
        Assert.Equal("Inline 2 methods", InlineMethodOperation.BuildAllFilesDescription(2));
    }


    [Fact]
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var first = InlineMethodOperation.FindMethod(
            root, "Log", line, ColumnOf(SameLineOverloadsSource, "Log(string message)"));
        var second = InlineMethodOperation.FindMethod(
            root, "Log", line, ColumnOf(SameLineOverloadsSource, "Log(int value)"));
        var omitted = InlineMethodOperation.FindMethod(root, "Log", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Equal(["message"], ParameterNames(first));
        Assert.Equal(["value"], ParameterNames(second));
    }

    [Fact]
    public void FindMethod_OmittedColumn_UsesIdentifierStartLine()
    {
        const string source = """
            class C
            {
                private void
                Log(string message) { System.Console.WriteLine(message); }

                private void Log(int value) { System.Console.WriteLine(value); }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "private void");
        var identifierLine = FindLine(source, "Log(string message)");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's identifier start-line filter — the
        // split signature still matches when line is the identifier line.
        var byIdentifierLine = InlineMethodOperation.FindMethod(root, "Log", identifierLine, column: null);
        var byDeclarationStart = InlineMethodOperation.FindMethod(root, "Log", startLine, column: null);

        Assert.NotNull(byIdentifierLine);
        Assert.Equal(["message"], ParameterNames(byIdentifierLine));
        Assert.Null(byDeclarationStart);
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                private void
                Log(string message) { System.Console.WriteLine(message); }

                private void Log(int value) { System.Console.WriteLine(value); }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "private void");
        var identifierLine = FindLine(source, "Log(string message)");
        Assert.NotEqual(startLine, identifierLine);

        // Column on the identifier line still selects the split signature
        // even though the declaration does not start on that line.
        var byColumn = InlineMethodOperation.FindMethod(
            root, "Log", identifierLine, ColumnOf(source, "Log(string message)"));
        var byDeclarationLineAndColumn = InlineMethodOperation.FindMethod(
            root, "Log", startLine, ColumnOf(source, "private void"));

        Assert.NotNull(byColumn);
        Assert.Equal(["message"], ParameterNames(byColumn));
        Assert.NotNull(byDeclarationLineAndColumn);
        Assert.Equal(["message"], ParameterNames(byDeclarationLineAndColumn));
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                private void Other(){System.Console.WriteLine("o");}private void Log(){System.Console.WriteLine("l");}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "private void Other");
        var secondStart = ColumnOf(source, "private void Log");
        var secondId = ColumnOf(source, "Log(){System.Console.WriteLine(\"l\");}");

        var atSecondStart = InlineMethodOperation.FindMethod(root, "Log", line, secondStart);
        var atSecondId = InlineMethodOperation.FindMethod(root, "Log", line, secondId);
        var atFirstId = InlineMethodOperation.FindMethod(root, "Other", line, ColumnOf(source, "Other()"));
        var firstAtSecondStart = InlineMethodOperation.FindMethod(root, "Other", line, secondStart);

        Assert.NotNull(atSecondStart);
        Assert.NotNull(atSecondId);
        Assert.NotNull(atFirstId);
        Assert.Equal("Log", atSecondStart.Identifier.Text);
        Assert.Equal("Log", atSecondId.Identifier.Text);
        Assert.Equal("Other", atFirstId.Identifier.Text);
        Assert.Null(firstAtSecondStart);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class C { private void A(){}private void B(){} }";
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

    [Fact]
    public void MatchesCallSiteLocation_TreatsEndAsExclusive()
    {
        const string source = "class C { void M() { Log();Log(); } void Log() {} }";
        const string filePath = "Calls.cs";

        var tree = CSharpSyntaxTree.ParseText(source);
        var first = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First();
        var span = first.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = project.AddDocument("Calls.cs", source, filePath: filePath);

        Assert.True(InlineMethodOperation.MatchesCallSiteLocation(
            document, first, new CallSiteLocation { File = filePath, Line = line, Column = startCol }));
        Assert.True(InlineMethodOperation.MatchesCallSiteLocation(
            document, first, new CallSiteLocation { File = filePath, Line = line, Column = endCol - 1 }));
        Assert.False(InlineMethodOperation.MatchesCallSiteLocation(
            document, first, new CallSiteLocation { File = filePath, Line = line, Column = endCol }));
        Assert.False(InlineMethodOperation.MatchesCallSiteLocation(
            document, first, new CallSiteLocation { File = filePath, Line = line, Column = startCol - 1 }));
    }

    [SkippableFact]
    public async Task InlineMethod_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineMethod_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Log(int value)");

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log(string message)", updated);
        Assert.DoesNotContain("void Log(int value)", updated);
        Assert.Contains(@"Log(""one"")", updated);
        Assert.Contains(@"System.Console.WriteLine(2)", updated);
        Assert.DoesNotContain("Log(2)", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Log(string message)");

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("void Log(string message)", updated);
        Assert.Contains("void Log(int value)", updated);
        Assert.Contains(@"System.Console.WriteLine(""one"")", updated);
        Assert.Contains("Log(2)", updated);
        Assert.DoesNotContain(@"Log(""one"")", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_ColumnOnContinuationLine_InlinesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                private void
                Log(string message)
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
            Line = FindLine(source, "Log(string message)"),
            Column = ColumnOf(source, "Log(string message)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("void Log", updated);
        Assert.Contains(@"System.Console.WriteLine(""one"")", updated);
        Assert.DoesNotContain(@"Log(""one"")", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_OmittedColumn_ContinuationLineIdentifier_StillInlines()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                private void
                Log(string message)
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
            Line = FindLine(source, "Log(string message)")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("void Log", updated);
        Assert.Contains(@"System.Console.WriteLine(""one"")", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_OmittedColumn_DeclarationStartLineOfSplitSignature_ThrowsMethodNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                private void
                Log(string message)
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
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Log",
                Line = FindLine(source, "private void")
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineMethod_AdjacentMethods_ColumnOnSecondDoesNotInlineFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                private void Other(){System.Console.WriteLine("o");}private void Log(){System.Console.WriteLine("l");}

                public void Run()
                {
                    Other();
                    Log();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = FindLine(source, "private void Other"),
            Column = ColumnOf(source, "Log(){System.Console.WriteLine(\"l\");}")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Other()", updated);
        Assert.DoesNotContain("void Log()", updated);
        Assert.Contains(@"System.Console.WriteLine(""l"")", updated);
        Assert.Contains("Other();", updated);
        Assert.DoesNotContain("Log();", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private void Foo(int x)
                {
                    System.Console.WriteLine(x);
                }

                private void Foo(int x, int y)
                {
                    System.Console.WriteLine(x + y);
                }

                public void Run()
                {
                    Foo(1);
                    Foo(1, 2);
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineMethod_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Log(int value)");

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("void Log(string message)", StringComparison.Ordinal) &&
            !change.AfterSnippet.Contains("void Log(int value)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains(@"System.Console.WriteLine(2)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains(@"Log(""one"")", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InlineMethod_Column_RemoveMethodFalse_LeavesSelectedMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Log(string message)");

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = line,
            Column = firstColumn,
            RemoveMethod = false
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log(string message)", updated);
        Assert.Contains("void Log(int value)", updated);
        Assert.Contains(@"System.Console.WriteLine(""one"")", updated);
        Assert.Contains("Log(2)", updated);
        Assert.DoesNotContain(@"Log(""one"")", updated);
    }

    [SkippableFact]
    public async Task InlineMethod_Column_CallSiteLocation_InlinesOnlyThatCall()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new InlineMethodOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "private void Log(string message)");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Log(string message)");
        var callSite = FindInvocationLocation(SameLineOverloadsSource, @"Log(""one"")");

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Log",
            Line = line,
            Column = firstColumn,
            CallSiteLocation = new CallSiteLocation
            {
                File = workspace.SourcePath,
                Line = callSite.Line,
                Column = callSite.Column
            }
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.ReferencesUpdated);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Log(string message)", updated);
        Assert.Contains("void Log(int value)", updated);
        Assert.Contains(@"System.Console.WriteLine(""one"")", updated);
        Assert.Contains("Log(2)", updated);
        Assert.DoesNotContain(@"Log(""one"")", updated);
    }

    #endregion


    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public void Run()
            {
                Log();
                Greet();
            }

            private void Log()
            {
                System.Console.WriteLine("log");
            }

            private void Greet()
            {
                System.Console.WriteLine("hi");
            }

            public virtual void SkipVirtual()
            {
                System.Console.WriteLine("virtual");
            }

            public void CallerOfVirtual()
            {
                SkipVirtual();
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public int Run()
            {
                return Double(21);
            }

            private int Double(int value)
            {
                return value * 2;
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public class FileC
        {
            public void Run()
            {
                Recurse();
            }

            private void NeverCalled()
            {
                System.Console.WriteLine("unused");
            }

            private void Recurse()
            {
                Recurse();
            }

            public abstract class Base
            {
                public abstract void Abs();
            }
        }
        """;

    [SkippableFact]
    public async Task InlineMethod_AllFilesFalse_InlinesOnlySpecifiedMethod()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            MethodName = "Log"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("System.Console.WriteLine(\"log\");", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("private void Log()", updatedA, StringComparison.Ordinal);
        Assert.Contains("private void Greet()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineMethod_OmittedAllFiles_KeepsSingleSiteInline()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public void Run()
                {
                    Log();
                }

                private void Log()
                {
                    System.Console.WriteLine("x");
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
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Console.WriteLine(\"x\");", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private void Log()", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_InlinesEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("System.Console.WriteLine(\"log\");", updatedA, StringComparison.Ordinal);
        Assert.Contains("System.Console.WriteLine(\"hi\");", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("private void Log()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("private void Greet()", updatedA, StringComparison.Ordinal);
        Assert.Contains("public virtual void SkipVirtual()", updatedA, StringComparison.Ordinal);
        Assert.Contains("return 21 * 2;", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("private int Double", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileB);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                AllFiles = false,
                MethodName = "Double"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesFalse_WithoutMethodName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileB);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileB);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                AllFiles = true,
                MethodName = "Double"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileB);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileB);
        var operation = new InlineMethodOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new InlineMethodParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task InlineMethod_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
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
            (c.AfterSnippet.Contains("System.Console.WriteLine(\"log\")", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("return 21 * 2;", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("FileC", "FileC2", StringComparison.Ordinal)
                .Replace("NeverCalled", "NeverCalled2", StringComparison.Ordinal)
                .Replace("Recurse", "Recurse2", StringComparison.Ordinal)));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
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
    public async Task InlineMethod_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("System.Console.WriteLine(\"log\");", updatedA, StringComparison.Ordinal);
        Assert.Contains("System.Console.WriteLine(\"hi\");", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("System.Console.WriteLine(\"log\");", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_SkipsVirtualRecursiveAndNoCallSites()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileC.cs", IneligibleFileC));
        var operation = new InlineMethodOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public virtual void SkipVirtual()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_LinkedDocument_CoalescesToOnePhysicalWrite()
    {
        const string sharedSource = """
            namespace TestApp;

            public class Shared
            {
                public void Run()
                {
                    Log();
                }

                private void Log()
                {
                    System.Console.WriteLine("shared");
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

        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.Contains("System.Console.WriteLine(\"shared\");", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private void Log()", updated, StringComparison.Ordinal);

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.Contains("System.Console.WriteLine(\"shared\");", texts[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InlineMethod_AllFilesTrue_LinkedDocument_InlinesSiblingProjectCallers()
    {
        // Call site lives only in ProjectB's AnchorB; SymbolFinder on ProjectA's
        // Shared method symbol would miss it without sibling-compilation collection.
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
                public static int Double(int x)
                {
                    return x * 2;
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
                public static int Use()
                {
                    return Shared.Double(21);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new InlineMethodOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new InlineMethodParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AnchorB.cs"]));

        var updatedShared = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.DoesNotContain("public static int Double", updatedShared, StringComparison.Ordinal);

        var updatedAnchorB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["AnchorB.cs"]));
        Assert.Contains("return 21 * 2;", updatedAnchorB, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared.Double", updatedAnchorB, StringComparison.Ordinal);

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.DoesNotContain("public static int Double", texts[0], StringComparison.Ordinal);
    }


    #endregion

    #region Helpers

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethod_" + Guid.NewGuid().ToString("N"));
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpInlineMethodLinked_" + Guid.NewGuid().ToString("N"));
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

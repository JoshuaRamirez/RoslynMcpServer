using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="MakeNonStaticOperation"/>.
/// </summary>
public class MakeNonStaticOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                SourceFile = "",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                SourceFile = "Types.cs",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidSelectionRange_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 2,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                AllFiles = false,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrSelection_DoesNotThrow()
    {
        MakeNonStaticOperation.Validate(new MakeNonStaticParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                AllFiles = true,
                StartLine = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.Validate(new MakeNonStaticParams
            {
                AllFiles = true,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Make method an instance method", MakeNonStaticOperation.BuildAllFilesDescription(1));
        Assert.Equal("Make 2 methods instance methods", MakeNonStaticOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task MakeNonStatic_StaticMethod_RemovesStaticAndUpdatesCallSites()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }

                public int Use()
                {
                    var other = new Calculator();
                    return Calculator.Add(1, 2) + Calculator.Add(3, 4) + Add(5, 6);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Add", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Add(int a, int b)", updated);
        Assert.DoesNotContain("public static int Add", updated);
        Assert.DoesNotContain("Calculator.Add(1, 2)", updated);
        Assert.DoesNotContain("Calculator.Add(3, 4)", updated);
        Assert.Contains("this.Add(1, 2)", updated);
        Assert.Contains("this.Add(3, 4)", updated);
        Assert.Contains("Add(5, 6)", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_MethodGroup_RewritesToInstanceReceiver()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Double(int x)
                {
                    return x * 2;
                }

                public void Use()
                {
                    System.Func<int, int> fromType = Calculator.Double;
                    System.Func<int, int> fromUnqualified = Double;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Double");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Double"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Double(int x)", updated);
        Assert.Contains("fromType = this.Double", updated);
        Assert.Contains("fromUnqualified = Double", updated);
        Assert.DoesNotContain("Calculator.Double", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_UniqueExternalReceiver_RewritesToThatInstance()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Consumer
            {
                public int Use(Calculator calc)
                {
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Add(int a, int b)", updated);
        Assert.Contains("return calc.Add(1, 2);", updated);
        Assert.DoesNotContain("Calculator.Add(1, 2)", updated);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task MakeNonStatic_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }

                public int Use()
                {
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.Description.Contains("Add"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task MakeNonStatic_AlreadyInstance_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.AlreadyInstance, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MakeNonStatic_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task MakeNonStatic_NonMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private static int _value;

                public static int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "_value");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MakeNonStatic_NoValidInstanceReceiver_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Consumer
            {
                public int Use()
                {
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.NoValidInstanceReceiver, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Add(int a, int b)", before);
        Assert.Contains("Calculator.Add(1, 2)", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_ExternMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static extern int Add(int a, int b);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static extern int Add(int a, int b);", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_StaticClass_Throws()
    {
        const string source = """
            namespace TestApp;

            public static class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static int Add(int a, int b)", before);
    }

    [Fact]
    public void MakeNonStatic_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            MakeNonStaticOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Review fold

    [SkippableFact]
    public async Task MakeNonStatic_GenericReceiverTypeArgumentMismatch_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Container<T>
            {
                public static T Get(T value)
                {
                    return value;
                }
            }

            public class Consumer
            {
                public string Use(Container<string> strings)
                {
                    return Container<int>.Get(1).ToString();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Get");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Get"
            }));

        Assert.Equal(ErrorCodes.NoValidInstanceReceiver, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Container<int>.Get(1)", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_MatchingGenericReceiver_RewritesToThatInstance()
    {
        const string source = """
            namespace TestApp;

            public class Container<T>
            {
                public static T Get(T value)
                {
                    return value;
                }
            }

            public class Consumer
            {
                public int Use(Container<int> ints)
                {
                    return Container<int>.Get(1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Get");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Get"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public T Get(T value)", updated);
        Assert.Contains("return ints.Get(1);", updated);
        Assert.DoesNotContain("Container<int>.Get(1)", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_HidingDerivedMethod_CastsThisToSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Base
            {
                public static int Value()
                {
                    return 1;
                }
            }

            public class Derived : Base
            {
                public int Value()
                {
                    return 2;
                }

                public int Use()
                {
                    return Base.Value();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Value");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Value"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Value()", updated);
        Assert.DoesNotContain("public static int Value()", updated);
        Assert.Contains("return ((Base)this).Value();", updated);
        Assert.DoesNotContain("return this.Value();", updated);
        Assert.DoesNotContain("return Base.Value();", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_DerivedWithoutHiding_RewritesToThis()
    {
        const string source = """
            namespace TestApp;

            public class Base
            {
                public static int Value()
                {
                    return 1;
                }
            }

            public class Derived : Base
            {
                public int Use()
                {
                    return Base.Value();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Value");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Value"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return this.Value();", updated);
        Assert.DoesNotContain("return Base.Value();", updated);
        Assert.DoesNotContain("((Base)this)", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_ConstructorInitializerThis_Throws()
    {
        const string source = """
            namespace TestApp;

            public class C
            {
                public static int CreateValue()
                {
                    return 1;
                }

                public C() : this(C.CreateValue())
                {
                }

                public C(int value)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "CreateValue");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "CreateValue"
            }));

        Assert.Equal(ErrorCodes.NoValidInstanceReceiver, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains(": this(C.CreateValue())", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_UnassignedLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Consumer
            {
                public int Use()
                {
                    Calculator calc;
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.NoValidInstanceReceiver, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Calculator calc;", before);
        Assert.Contains("return Calculator.Add(1, 2);", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AssignedLocal_RewritesToThatInstance()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Consumer
            {
                public int Use()
                {
                    var calc = new Calculator();
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return calc.Add(1, 2);", updated);
        Assert.DoesNotContain("return Calculator.Add(1, 2);", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_NestedFunctionInStaticMember_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }

                public static int Use()
                {
                    System.Func<int> local = () => Calculator.Add(1, 2);
                    return local();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Add");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.NoValidInstanceReceiver, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("() => Calculator.Add(1, 2)", before);
    }

    [SkippableFact]
    public async Task MakeNonStatic_VerbatimKeywordReceiver_PreservesAtPrefix()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Consumer
            {
                public int Use(Calculator @class)
                {
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindIdentifierSpan(source, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return @class.Add(1, 2);", updated);
        Assert.DoesNotContain("return class.Add(1, 2);", updated);
        Assert.DoesNotContain("return Calculator.Add(1, 2);", updated);
    }

    #endregion

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public static int Add(int a, int b)
            {
                return a + b;
            }

            public int Use()
            {
                var other = new FileA();
                return FileA.Add(1, 2);
            }
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public static int Double(int x)
            {
                return x * 2;
            }

            public int Use()
            {
                return FileB.Double(2);
            }
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public interface ILogger
        {
            void Log();
            static void InterfaceHelper()
            {
            }
        }

        public static class StaticHelpers
        {
            public static int Helper(int x)
            {
                return x;
            }
        }

        public static class FileCExtensions
        {
            public static int Ext(this FileC c)
            {
                return 1;
            }
        }

        public class FileC : ILogger
        {
            public int Already(int a, int b)
            {
                return a + b;
            }

            public virtual int VirtualAdd(int a, int b)
            {
                return a + b;
            }

            public static int NoReceiver(int a, int b)
            {
                return a + b;
            }

            public void Log()
            {
            }
        }

        public static class FileCCaller
        {
            public static int Use()
            {
                return FileC.NoReceiver(1, 2);
            }
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public class Mixed
        {
            public static int Eligible(int a, int b)
            {
                return a + b;
            }

            public int UseEligible()
            {
                return Mixed.Eligible(1, 2);
            }

            public int Already(int a, int b)
            {
                return a + b;
            }

            public static int NoReceiver(int a, int b)
            {
                return a + b;
            }

            public virtual int VirtualAdd(int a, int b)
            {
                return a + b;
            }
        }

        public static class MixedCaller
        {
            public static int Use()
            {
                return Mixed.NoReceiver(1, 2);
            }
        }
        """;

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesFalse_MakesOnlySpecifiedMethod()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(EligibleFileA, "Add");
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Add(int a, int b)", updatedA);
        Assert.DoesNotContain("public static int Add", updatedA);
        Assert.Contains("this.Add(1, 2)", updatedA);
        Assert.DoesNotContain("FileA.Add(1, 2)", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_OmittedAllFiles_KeepsSingleSiteMakeNonStatic()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new MakeNonStaticOperation(workspace.Context);
        var span = FindSpan(EligibleFileA, "Add");

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "Add"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Add(int a, int b)", updated);
        Assert.DoesNotContain("public static int Add", updated);
        Assert.Contains("this.Add(1, 2)", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_MakesEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public int Add(int a, int b)", updatedA);
        Assert.DoesNotContain("public static int Add", updatedA);
        Assert.Contains("this.Add(1, 2)", updatedA);
        Assert.DoesNotContain("FileA.Add(1, 2)", updatedA);
        Assert.Contains("public int Double(int x)", updatedB);
        Assert.DoesNotContain("public static int Double", updatedB);
        Assert.Contains("this.Double(2)", updatedB);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_WithoutSourceFileOrSelection_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new MakeNonStaticOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new MakeNonStaticOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                AllFiles = false,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesFalse_WithoutSelection_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new MakeNonStaticOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new MakeNonStaticOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                AllFiles = true,
                StartLine = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_WithSymbolName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new MakeNonStaticOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MakeNonStaticParams
            {
                AllFiles = true,
                SymbolName = "Add"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MakeNonStatic_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
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
            c.Description.Contains("instance", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("int Add", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("int Double", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC.Replace("FileC", "FileC2", StringComparison.Ordinal)
                .Replace("ILogger", "ILogger2", StringComparison.Ordinal)));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
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
    public async Task MakeNonStatic_AllFilesTrue_SkipsAlreadyInstanceExtensionVirtualInterfaceStaticClassAndNoReceiver()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new MakeNonStaticOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("public int Eligible(int a, int b)", updated);
        Assert.DoesNotContain("public static int Eligible", updated);
        Assert.Contains("this.Eligible(1, 2)", updated);
        Assert.Contains("public int Already(int a, int b)", updated);
        Assert.Contains("public static int NoReceiver(int a, int b)", updated);
        Assert.Contains("public virtual int VirtualAdd(int a, int b)", updated);
        Assert.DoesNotContain("static virtual", updated);
        Assert.DoesNotContain("virtual static", updated);
        Assert.Contains("return Mixed.NoReceiver(1, 2);", updated);
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Add(int a, int b)", updatedA);
        Assert.DoesNotContain("public static int Add", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public int Add(int a, int b)", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_CalledPartialMethod_MakesBothDeclarationsInstance()
    {
        const string definition = """
            namespace TestApp;

            public partial class Calculator
            {
                public static partial int Add(int a, int b);

                public int Use()
                {
                    var other = new Calculator();
                    return Calculator.Add(1, 2);
                }
            }
            """;

        const string implementation = """
            namespace TestApp;

            public partial class Calculator
            {
                public static partial int Add(int a, int b) => a + b;
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Calculator.Definition.cs", definition),
            ("Calculator.Implementation.cs", implementation));
        var operation = new MakeNonStaticOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedDef = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Definition.cs"]));
        var updatedImpl = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Implementation.cs"]));
        Assert.Contains("public partial int Add(int a, int b);", updatedDef);
        Assert.DoesNotContain("public static partial int Add(int a, int b);", updatedDef);
        Assert.Contains("this.Add(1, 2)", updatedDef);
        Assert.DoesNotContain("Calculator.Add(1, 2)", updatedDef);
        Assert.Contains("public partial int Add(int a, int b)", updatedImpl);
        Assert.DoesNotContain("public static partial int Add(int a, int b)", updatedImpl);
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["Calculator.Definition.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Calculator.Implementation.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_PreviewAllFiles_PartialMethod_DescribesBothDeclarationFiles()
    {
        const string definition = """
            namespace TestApp;

            public partial class Calculator
            {
                public static partial int Add(int a, int b);

                public int Use()
                {
                    var other = new Calculator();
                    return Calculator.Add(1, 2);
                }
            }
            """;

        const string implementation = """
            namespace TestApp;

            public partial class Calculator
            {
                public static partial int Add(int a, int b) => a + b;
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Calculator.Definition.cs", definition),
            ("Calculator.Implementation.cs", implementation));
        var operation = new MakeNonStaticOperation(workspace.Context);
        var beforeDef = await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Definition.cs"]);
        var beforeImpl = await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Implementation.cs"]);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        var def = Assert.Single(result.PendingChanges, c =>
            PathEquals(c.File, workspace.SourcePaths["Calculator.Definition.cs"]));
        var impl = Assert.Single(result.PendingChanges, c =>
            PathEquals(c.File, workspace.SourcePaths["Calculator.Implementation.cs"]));
        Assert.Equal("Make method an instance method", def.Description);
        Assert.Equal("Make method an instance method", impl.Description);
        Assert.Equal(beforeDef, await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Definition.cs"]));
        Assert.Equal(beforeImpl, await File.ReadAllTextAsync(workspace.SourcePaths["Calculator.Implementation.cs"]));
    }

    [SkippableFact]
    public async Task MakeNonStatic_AllFilesTrue_SkipsConditionalAccess()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static void Log()
                {
                }

                public void Use(Calculator? other)
                {
                    other?.Log();
                }

                public static int Add(int a, int b)
                {
                    return a + b;
                }

                public int UseAdd()
                {
                    return Calculator.Add(1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MakeNonStaticOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MakeNonStaticParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Add(int a, int b)", updated);
        Assert.DoesNotContain("public static int Add", updated);
        // other?.Log() already has an instance receiver, so Log is eligible
        // (inverse of make_static, where ?. would drop short-circuiting).
        Assert.Contains("public void Log()", updated);
        Assert.DoesNotContain("public static void Log()", updated);
        Assert.Contains("other?.Log();", updated);
        Assert.Contains("this.Add(1, 2)", updated);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpMakeNonStaticMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

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

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

    /// <summary>
    /// Finds the first identifier occurrence that is a standalone token, not a
    /// prefix of a longer identifier (for example <c>Get</c> in <c>Get()</c>
    /// rather than the <c>Get</c> inside <c>GetHashCode</c>).
    /// </summary>
    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindIdentifierSpan(
        string source,
        string identifier)
    {
        var start = 0;
        while (start < source.Length)
        {
            var index = source.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException($"Identifier not found: {identifier}");

            var beforeOk = index == 0 || !IsIdentifierPart(source[index - 1]);
            var afterIndex = index + identifier.Length;
            var afterOk = afterIndex >= source.Length || !IsIdentifierPart(source[afterIndex]);
            if (beforeOk && afterOk)
            {
                return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
                    GetLineColumn(source, afterIndex).Line, GetLineColumn(source, afterIndex).Column);
            }

            start = index + 1;
        }

        throw new InvalidOperationException($"Identifier not found: {identifier}");
    }

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static (int Line, int Column) GetLineColumn(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMakeNonStatic_" + Guid.NewGuid().ToString("N"));
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
                    SourcePath = sourcePaths[files[0].FileName],
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="IntroduceFieldOperation"/>.
/// </summary>
public class IntroduceFieldOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = "",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = "Types.cs",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidSelectionRange_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 2,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "123bad"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrFieldName_DoesNotThrow()
    {
        IntroduceFieldOperation.Validate(new IntroduceFieldParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                AllFiles = true,
                FieldName = "_value"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("fieldName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                AllFiles = true,
                StartLine = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSpan_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                AllFiles = true,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                AllFiles = true,
                SourceFile = "Types.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            IntroduceFieldOperation.Validate(new IntroduceFieldParams
            {
                AllFiles = true,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceFieldAllFilesFalse.cs");
        File.WriteAllText(file, "//");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                IntroduceFieldOperation.Validate(new IntroduceFieldParams
                {
                    AllFiles = false,
                    SourceFile = file,
                    FieldName = "_value",
                    StartColumn = 1,
                    EndLine = 1,
                    EndColumn = 2
                }));

            Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
            Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Introduce field", IntroduceFieldOperation.BuildAllFilesDescription(1));
        Assert.Equal("Introduce 2 fields", IntroduceFieldOperation.BuildAllFilesDescription(2));
    }


    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task IntroduceField_Literal_CreatesFieldAndReplacesExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _answer = 42;", updated);
        Assert.Contains("return this._answer;", updated);
        Assert.DoesNotContain("return 42;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_Expression_CreatesFieldAndReplacesExpression()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 1 + 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "1 + 2");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_sum"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _sum = 1 + 2;", updated);
        Assert.Contains("return this._sum;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalVariable_PromotesToField()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    int value = 7;
                    return value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "value = 7");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_value"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _value = 7;", updated);
        Assert.Contains("return this._value;", updated);
        Assert.DoesNotContain("int value = 7;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInConstructor_AddsAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            InitializeInConstructor = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _answer;", updated);
        Assert.DoesNotContain("private int _answer = 42;", updated);
        Assert.Contains("public Calculator()", updated);
        Assert.Contains("this._answer = 42;", updated);
        Assert.Contains("return this._answer;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_InitializeInExistingConstructor_PrependsAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public Calculator()
                {
                    Warmup();
                }

                public int Get()
                {
                    return 42;
                }

                private static void Warmup()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            InitializeInConstructor = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("this._answer = 42;", updated);
        Assert.Contains("Warmup();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public Calculator()"));
    }

    [SkippableFact]
    public async Task IntroduceField_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.Description.Contains("_answer"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task IntroduceField_Readonly_AddsModifier()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            IsReadonly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private readonly int _answer = 42;", updated);
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task IntroduceField_NoExpression_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFound, ex.ErrorCode);
    }

    [Fact]
    public void IntroduceField_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_InterfaceTarget_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ICalculator
            {
                int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_NameExists_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _answer = 1;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_ExpressionUsesLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    int offset = 3;
                    return offset + 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "offset + 2");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_sum"
            }));

        Assert.Equal(ErrorCodes.ExpressionCapturesLocal, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task IntroduceField_InstanceFieldFromStaticMember_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_answer"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
    }

    #endregion

    #region Review follow-up regressions

    [SkippableFact]
    public async Task IntroduceField_ReplaceAll_DoesNotRewriteDifferentBindings()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; }

                public int First()
                {
                    return Value + 1;
                }

                public int Second()
                {
                    int Value = 10;
                    return Value + 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "Value + 1");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_next",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _next = Value + 1;", updated);
        Assert.Contains("return this._next;", updated);
        Assert.Contains("int Value = 10;", updated);
        Assert.Contains("return Value + 1;", updated);
        Assert.Equal(1, CountOccurrences(updated, "return this._next;"));
        Assert.Equal(1, CountOccurrences(updated, "return Value + 1;"));
    }

    [SkippableFact]
    public async Task IntroduceField_ReplaceAll_RewritesSameBindings()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; }

                public int First()
                {
                    return Value + 1;
                }

                public int Second()
                {
                    return Value + 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "Value + 1");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_next",
            ReplaceAll = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _next = Value + 1;", updated);
        Assert.Equal(2, CountOccurrences(updated, "return this._next;"));
        Assert.DoesNotContain("return Value + 1;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_ShadowingParameter_UsesThisQualifiedReference()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    int value = 7;
                    return Apply(x =>
                    {
                        int _value = x;
                        return value + _value;
                    });
                }

                private static int Apply(System.Func<int, int> fn)
                {
                    return fn(1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "value = 7");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_value"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _value = 7;", updated);
        Assert.Contains("return this._value + _value;", updated);
        Assert.DoesNotContain("return value + _value;", updated);
        Assert.DoesNotContain("int value = 7;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_StaticField_UsesTypeQualifiedReference()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public static int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer",
            IsStatic = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static int _answer = 42;", updated);
        Assert.Contains("return Calculator._answer;", updated);
        Assert.DoesNotContain("return 42;", updated);
    }

    [SkippableFact]
    public async Task IntroduceField_UsingLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public sealed class Resource : System.IDisposable
            {
                public int Value => 1;
                public void Dispose()
                {
                }
            }

            public class Calculator
            {
                public int Get()
                {
                    using var resource = new Resource();
                    return resource.Value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "resource = new Resource()");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_resource"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("using var resource = new Resource();", unchanged);
        Assert.DoesNotContain("private Resource _resource", unchanged);
    }

    [SkippableFact]
    public async Task IntroduceField_AwaitUsingLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public sealed class Resource : System.IAsyncDisposable
            {
                public int Value => 1;
                public System.Threading.Tasks.ValueTask DisposeAsync()
                {
                    return System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }

            public class Calculator
            {
                public async System.Threading.Tasks.Task<int> Get()
                {
                    await using var resource = new Resource();
                    return resource.Value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "resource = new Resource()");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_resource"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("await using var resource = new Resource();", unchanged);
    }

    [SkippableFact]
    public async Task IntroduceField_StaticInitializer_InsertsNewFieldBeforeSource()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private static int A = Compute();

                private static int Compute()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "Compute()");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "B",
            IsStatic = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static int B = Compute();", updated);
        Assert.Contains("private static int A = Calculator.B;", updated);
        var bIndex = updated.IndexOf("private static int B = Compute();", StringComparison.Ordinal);
        var aIndex = updated.IndexOf("private static int A = Calculator.B;", StringComparison.Ordinal);
        Assert.True(bIndex >= 0 && aIndex > bIndex, "New static field B must be declared before A.");
    }

    [SkippableFact]
    public async Task IntroduceField_DuplicateTypeName_UpdatesSelectedTypeOnly()
    {
        const string source = """
            namespace TestApp
            {
                public class Calculator
                {
                    public int Get()
                    {
                        return 42;
                    }
                }
            }

            namespace Other
            {
                public class Calculator
                {
                    public int Get()
                    {
                        return 99;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "99");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_answer"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "private int _answer = 99;"));
        Assert.Contains("return this._answer;", updated);
        Assert.Contains("return 42;", updated);
        Assert.DoesNotContain("return 99;", updated);

        var testAppIndex = updated.IndexOf("namespace TestApp", StringComparison.Ordinal);
        var otherIndex = updated.IndexOf("namespace Other", StringComparison.Ordinal);
        var fieldIndex = updated.IndexOf("private int _answer = 99;", StringComparison.Ordinal);
        Assert.True(testAppIndex >= 0 && otherIndex > testAppIndex);
        Assert.True(fieldIndex > otherIndex, "Field must be inserted into Other.Calculator, not TestApp.Calculator.");
    }

    #endregion

    #region Helpers


    #region allFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Run()
            {
                int total = 1 + 2;
                string greeting = "hi";
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

            public int ExpressionOnly()
            {
                return 42;
            }
        }
        """;

    [SkippableFact]
    public async Task IntroduceField_OmittedAllFiles_KeepsSingleSitePromote()
    {
        const string source = """
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

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "total = 1 + 2");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_total"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _total", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("int total = 1 + 2;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesEligibleLocalsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceFieldOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("private int total = 1 + 2;", updatedA, StringComparison.Ordinal);
        Assert.Contains("private string greeting = \"hi\";", updatedA, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("                int total", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("                string greeting", updatedA, StringComparison.Ordinal);
        Assert.Contains("private int capacity = 10;", updatedB, StringComparison.Ordinal);
        Assert.Contains("return this.capacity;", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("                int capacity", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_WithoutSourceFileOrFieldName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                AllFiles = false,
                FieldName = "_total",
                StartLine = 8,
                StartColumn = 1,
                EndLine = 8,
                EndColumn = 5
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_WithFieldName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                AllFiles = true,
                FieldName = "_total"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("fieldName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                AllFiles = true,
                StartLine = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("startLine", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task IntroduceField_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceFieldOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
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
    public async Task IntroduceField_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new IntroduceFieldOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("private int total", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new IntroduceFieldOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceField_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class Outside { void M() { int value = 1; } }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new IntroduceFieldParams
                {
                    AllFiles = true,
                    SourceFile = outsidePath
                }));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }


    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsWithMethodTypeParameters()
    {
        const string source = """
            namespace TestApp;

            using System.Collections.Generic;

            public class Calculator
            {
                public T Generic<T>()
                {
                    T value = default!;
                    List<T> items = new();
                    return value;
                }

                public int Run()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("T value = default!;", updated, StringComparison.Ordinal);
        Assert.Contains("List<T> items = new();", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private T value", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private List<T> items", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_Readonly_SkipsMutatedLocals()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Mutated()
                {
                    int count = 0;
                    count++;
                    return count;
                }

                public void RefOut(ref int sink)
                {
                    int temp = 1;
                    sink = temp;
                    Set(out temp);
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }

                private static void Set(out int value) => value = 2;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true,
            IsReadonly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private readonly int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("int count = 0;", updated, StringComparison.Ordinal);
        Assert.Contains("count++;", updated, StringComparison.Ordinal);
        Assert.Contains("int temp = 1;", updated, StringComparison.Ordinal);
        Assert.Contains("Set(out temp);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly int count", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly int temp", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsCapturingInstanceMembersInline()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; } = 5;

                public int CaptureThis()
                {
                    int n = this.Value;
                    return n;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("int n = this.Value;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int n = this.Value;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsCapturingGenericInstanceCallsInline()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public T Get<T>() => default!;

                public int CaptureGeneric()
                {
                    int n = Get<int>();
                    return n;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("int n = Get<int>();", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int n = Get<int>();", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsCapturingImplicitConditionalAccessInline()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string? Name { get; set; } = "abc";

                public int CaptureConditional()
                {
                    int len = Name?.Length ?? 0;
                    return len;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("int len = Name?.Length ?? 0;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int len = Name?.Length ?? 0;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithOtherObjectInstanceAccess()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int DayNumber()
                {
                    int n = System.DateTime.Now.Day;
                    return n;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("private int n = System.DateTime.Now.Day;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.n;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithNameofInstanceMember()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; }

                public string CaptureName()
                {
                    string name = nameof(Value);
                    return name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string name = nameof(Value);", updated, StringComparison.Ordinal);
        Assert.Contains("return this.name;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    string name = nameof(Value);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalCapturingGenericInstanceCallInline_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public T Get<T>() => default!;

                public int CaptureGeneric()
                {
                    int n = Get<int>();
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "n = Get<int>()");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_n"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int n = Get<int>();", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _n", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalCapturingImplicitConditionalAccessInline_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public string? Name { get; set; } = "abc";

                public int CaptureConditional()
                {
                    int len = Name?.Length ?? 0;
                    return len;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "len = Name?.Length ?? 0");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_len"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int len = Name?.Length ?? 0;", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _len", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithOtherObjectInstanceAccess_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int DayNumber()
                {
                    int n = System.DateTime.Now.Day;
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "n = System.DateTime.Now.Day");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_n"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int _n = System.DateTime.Now.Day;", updated, StringComparison.Ordinal);
        Assert.Contains("return this._n;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithNameofInstanceMember_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; }

                public string CaptureName()
                {
                    string name = nameof(Value);
                    return name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "name = nameof(Value)");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_name"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string _name = nameof(Value);", updated, StringComparison.Ordinal);
        Assert.Contains("return this._name;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    string name = nameof(Value);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithTypeofTypeParameter()
    {
        const string source = """
            namespace TestApp;

            public class Calculator<T>
            {
                public System.Type CaptureType()
                {
                    System.Type type = typeof(T);
                    return type;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private System.Type type = typeof(T);", updated, StringComparison.Ordinal);
        Assert.Contains("return this.type;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    System.Type type = typeof(T);", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithTypeofTypeParameter_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Calculator<T>
            {
                public System.Type CaptureType()
                {
                    System.Type type = typeof(T);
                    return type;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "type = typeof(T)");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_type"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private System.Type _type = typeof(T);", updated, StringComparison.Ordinal);
        Assert.Contains("return this._type;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    System.Type type = typeof(T);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsWithTypeofMethodTypeParameter()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public System.Type CaptureMethodType<T>()
                {
                    System.Type type = typeof(T);
                    return type;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("System.Type type = typeof(T);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private System.Type type = typeof(T);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithTypeofMethodTypeParameter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public System.Type CaptureMethodType<T>()
                {
                    System.Type type = typeof(T);
                    return type;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "type = typeof(T)");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_type"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("System.Type type = typeof(T);", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private System.Type _type", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_ExpressionWithTypeofMethodTypeParameter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public System.Type CaptureMethodType<T>()
                {
                    return typeof(T);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "typeof(T)");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_type"
            }));

        Assert.Equal(ErrorCodes.InvalidTargetType, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("return typeof(T);", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private System.Type _type", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithNestedLocalFunctionTypeParameter()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public System.Action CaptureNested()
                {
                    System.Action action = () =>
                    {
                        void F<T>()
                        {
                            _ = typeof(T);
                        }

                        F<int>();
                    };
                    return action;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private System.Action action = () =>", updated, StringComparison.Ordinal);
        Assert.Contains("void F<T>()", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    System.Action action = () =>", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithNestedLocalFunctionTypeParameter_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public System.Action CaptureNested()
                {
                    System.Action action = () =>
                    {
                        void F<T>()
                        {
                            _ = typeof(T);
                        }

                        F<int>();
                    };
                    return action;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "action = () =>");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_action"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private System.Action _action = () =>", updated, StringComparison.Ordinal);
        Assert.Contains("void F<T>()", updated, StringComparison.Ordinal);
        Assert.Contains("return this._action;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    System.Action action = () =>", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsCallingEnclosingStaticLocalFunction()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int CaptureLocalFunction()
                {
                    static int Get() => 1;
                    int number = Get();
                    return number;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int number = Get();", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int number = Get();", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalCallingEnclosingStaticGenericLocalFunction_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int CaptureLocalFunction()
                {
                    static T Get<T>(T value) => value;
                    int number = Get<int>(1);
                    return number;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "number = Get<int>(1)");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_number"
            }));

        Assert.Equal(ErrorCodes.ExpressionCapturesLocal, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int number = Get<int>(1);", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _number", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithPropertyPattern()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public int Value { get; set; }
            }

            public class Calculator
            {
                public bool Match()
                {
                    bool matches = new Widget() is { Value: 1 };
                    return matches;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private bool matches = new Widget()", updated, StringComparison.Ordinal);
        Assert.Contains("is { Value: 1 }", updated, StringComparison.Ordinal);
        Assert.Contains("return this.matches;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    bool matches = new Widget()", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithPropertyPattern_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public int Value { get; set; }
            }

            public class Calculator
            {
                public bool Match()
                {
                    bool matches = new Widget() is { Value: 1 };
                    return matches;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "matches = new Widget() is { Value: 1 }");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_matches"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private bool _matches = new Widget()", updated, StringComparison.Ordinal);
        Assert.Contains("is { Value: 1 }", updated, StringComparison.Ordinal);
        Assert.Contains("return this._matches;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("bool matches = new Widget()", updated, StringComparison.Ordinal);
    }


    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithExtendedPropertyPattern()
    {
        const string source = """
            namespace TestApp;

            public class Node
            {
                public Node Child { get; set; } = null!;
                public int Value { get; set; }
            }

            public class Calculator
            {
                public bool Match()
                {
                    bool matches = new Node() is { Child.Value: 1 };
                    return matches;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private bool matches = new Node()", updated, StringComparison.Ordinal);
        Assert.Contains("is { Child.Value: 1 }", updated, StringComparison.Ordinal);
        Assert.Contains("return this.matches;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("                    bool matches = new Node()", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithExtendedPropertyPattern_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Node
            {
                public Node Child { get; set; } = null!;
                public int Value { get; set; }
            }

            public class Calculator
            {
                public bool Match()
                {
                    bool matches = new Node() is { Child.Value: 1 };
                    return matches;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "matches = new Node() is { Child.Value: 1 }");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_matches"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private bool _matches = new Node()", updated, StringComparison.Ordinal);
        Assert.Contains("is { Child.Value: 1 }", updated, StringComparison.Ordinal);
        Assert.Contains("return this._matches;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("bool matches = new Node()", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_PromotesLocalsWithObjectInitializer()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public int Value { get; set; }
            }

            public class Calculator
            {
                public Widget MakeCopy()
                {
                    Widget copy = new Widget { Value = 1 };
                    return copy;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private TestApp.Widget copy = new Widget", updated, StringComparison.Ordinal);
        Assert.Contains("Value = 1", updated, StringComparison.Ordinal);
        Assert.Contains("return this.copy;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget copy = new Widget { Value = 1 };", updated, StringComparison.Ordinal);
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithObjectInitializer_Promotes()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public int Value { get; set; }
            }

            public class Calculator
            {
                public Widget MakeCopy()
                {
                    Widget copy = new Widget { Value = 1 };
                    return copy;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "copy = new Widget { Value = 1 }");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_copy"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private TestApp.Widget _copy = new Widget", updated, StringComparison.Ordinal);
        Assert.Contains("Value = 1", updated, StringComparison.Ordinal);
        Assert.Contains("return this._copy;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget copy = new Widget { Value = 1 };", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithWithInitializer_Promotes()
    {
        const string source = """
            namespace TestApp;

            public record Widget
            {
                public int Value { get; init; }
            }

            public class Calculator
            {
                public Widget MakeCopy()
                {
                    Widget copy = new Widget { Value = 0 } with { Value = 1 };
                    return copy;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "copy = new Widget { Value = 0 } with { Value = 1 }");

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            FieldName = "_copy"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private TestApp.Widget _copy = new Widget", updated, StringComparison.Ordinal);
        Assert.Contains("with", updated, StringComparison.Ordinal);
        Assert.Contains("Value = 1", updated, StringComparison.Ordinal);
        Assert.Contains("return this._copy;", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Widget copy = new Widget { Value = 0 } with { Value = 1 };", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsConstLocals()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int SwitchOnConst(int input)
                {
                    const int label = 1;
                    switch (input)
                    {
                        case label:
                            return label;
                        default:
                            return 0;
                    }
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("const int label = 1;", updated, StringComparison.Ordinal);
        Assert.Contains("case label:", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int label", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("case this.label:", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsRefLocals()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private static int[] StaticStorage = { 1, 2, 3 };

                public int Alias()
                {
                    ref int alias = ref StaticStorage[0];
                    return alias;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("ref int alias = ref StaticStorage[0];", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int alias = ref StaticStorage[0];", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private int alias", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalCapturingInstanceMembersInline_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; } = 5;

                public int CaptureThis()
                {
                    int n = this.Value;
                    return n;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "n = this.Value");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_n"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int n = this.Value;", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _n", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_LocalWithEscapedNameofThis_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; } = 5;

                public static string @nameof(object value) => value?.ToString() ?? "";

                public string CaptureEscapedNameof()
                {
                    string name = @nameof(this.Value);
                    return name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "name = @nameof(this.Value)");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_name"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("string name = @nameof(this.Value);", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private string _name", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_AllFilesTrue_SkipsLocalsWithEscapedNameofThis()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Value { get; set; } = 5;

                public static string @nameof(object value) => value?.ToString() ?? "";

                public string CaptureEscapedNameof()
                {
                    string name = @nameof(this.Value);
                    return name;
                }

                public int Clean()
                {
                    int total = 1 + 2;
                    return total;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new IntroduceFieldParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int total = 1 + 2;", updated, StringComparison.Ordinal);
        Assert.Contains("return this.total;", updated, StringComparison.Ordinal);
        Assert.Contains("string name = @nameof(this.Value);", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private string name = @nameof(this.Value);", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_ConstLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int SwitchOnConst(int input)
                {
                    const int label = 1;
                    switch (input)
                    {
                        case label:
                            return label;
                        default:
                            return 0;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "label = 1");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_label"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("const int label = 1;", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _label", unchanged, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task IntroduceField_RefLocal_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private static int[] StaticStorage = { 1, 2, 3 };

                public int Alias()
                {
                    ref int alias = ref StaticStorage[0];
                    return alias;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new IntroduceFieldOperation(workspace.Context);
        var span = FindSpan(source, "alias = ref StaticStorage[0]");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new IntroduceFieldParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                FieldName = "_alias"
            }));

        Assert.Equal(ErrorCodes.ExpressionNotFieldInitializable, ex.ErrorCode);
        var unchanged = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("ref int alias = ref StaticStorage[0];", unchanged, StringComparison.Ordinal);
        Assert.DoesNotContain("private int _alias", unchanged, StringComparison.Ordinal);
    }

    #endregion


    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string AbsoluteTestPath() =>
        OperatingSystem.IsWindows() ? @"C:\test\file.cs" : "/test/file.cs";

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

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
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileAsync(files);

        public static async Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpIntroduceField_" + Guid.NewGuid().ToString("N"));
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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="GenerateMethodStubOperation"/>,
/// including <c>throwNotImplemented</c> and <c>replaceExisting</c>.
/// </summary>
public class GenerateMethodStubOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = "",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = "Types.cs",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 0,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
            {
                SourceFile = AbsoluteTestPath(),
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidVisibility_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubVisibility.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
                {
                    SourceFile = path,
                    Line = 1,
                    Column = 1,
                    Visibility = "secret"
                }));

            Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_ReservedKeywordMethodName_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubKeyword.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                GenerateMethodStubOperation.Validate(new GenerateMethodStubParams
                {
                    SourceFile = path,
                    Line = 1,
                    Column = 1,
                    MethodName = "class"
                }));

            Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidIdentifier_ReservedKeyword_IsFalse()
    {
        Assert.False(GenerateMethodStubOperation.IsValidIdentifier("class"));
        Assert.False(GenerateMethodStubOperation.IsValidIdentifier("namespace"));
        Assert.True(GenerateMethodStubOperation.IsValidIdentifier("DoWork"));
        Assert.True(GenerateMethodStubOperation.IsValidIdentifier("@class"));
    }

    [Fact]
    public void ThrowNotImplemented_DefaultsToTrue()
    {
        var @params = new GenerateMethodStubParams
        {
            SourceFile = AbsoluteTestPath(),
            Line = 1,
            Column = 1
        };

        Assert.True(@params.ThrowNotImplemented);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new GenerateMethodStubParams
        {
            SourceFile = AbsoluteTestPath(),
            Line = 1,
            Column = 1
        };

        Assert.False(@params.ReplaceExisting);
    }

    #endregion

    #region ResolveMethodToReplace

    [Fact]
    public void ResolveMethodToReplace_OmittedFlag_ExistingCompatible_Throws()
    {
        var type = CompileType("""
            public class Widget
            {
                public void DoWork() { }
            }
            """, "Widget");

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.ResolveMethodToReplace(
                type,
                "DoWork",
                Array.Empty<GenerateMethodStubOperation.InferredParameter>(),
                typeParameterCount: 0,
                replaceExisting: false));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void ResolveMethodToReplace_SingleOrdinaryMethod_ReturnsIt()
    {
        var type = CompileType("""
            public class Widget
            {
                public void DoWork() { }
            }
            """, "Widget");

        var existing = GenerateMethodStubOperation.ResolveMethodToReplace(
            type,
            "DoWork",
            Array.Empty<GenerateMethodStubOperation.InferredParameter>(),
            typeParameterCount: 0,
            replaceExisting: true);

        Assert.NotNull(existing);
        Assert.Equal("DoWork", existing.Name);
        Assert.Equal(MethodKind.Ordinary, existing.MethodKind);
    }

    [Fact]
    public void ResolveMethodToReplace_TwoCompatibleByName_ThrowsNameCollision()
    {
        var type = CompileType("""
            public class @int { }

            public class Widget
            {
                public void DoWork(int value) { }
                public void DoWork(@int value) { }
            }
            """, "Widget");

        var parameters = new[]
        {
            new GenerateMethodStubOperation.InferredParameter("value", "int", RefKind.None)
        };

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.ResolveMethodToReplace(
                type, "DoWork", parameters, typeParameterCount: 0, replaceExisting: true));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("Multiple methods named 'DoWork'", ex.Message);
    }

    [Fact]
    public void ResolveMethodToReplace_RefKindDistinguishesOverloads()
    {
        var type = CompileType("""
            public class Widget
            {
                public void Process(int value) { }
                public void Process(ref int value) { }
            }
            """, "Widget");

        var byValue = GenerateMethodStubOperation.ResolveMethodToReplace(
            type,
            "Process",
            new[] { new GenerateMethodStubOperation.InferredParameter("value", "int", RefKind.None) },
            typeParameterCount: 0,
            replaceExisting: true);
        var byRef = GenerateMethodStubOperation.ResolveMethodToReplace(
            type,
            "Process",
            new[] { new GenerateMethodStubOperation.InferredParameter("value", "int", RefKind.Ref) },
            typeParameterCount: 0,
            replaceExisting: true);

        Assert.NotNull(byValue);
        Assert.Equal(RefKind.None, byValue.Parameters[0].RefKind);
        Assert.NotNull(byRef);
        Assert.Equal(RefKind.Ref, byRef.Parameters[0].RefKind);
        Assert.False(SymbolEqualityComparer.Default.Equals(byValue, byRef));
    }

    [Fact]
    public void ResolveMethodToReplace_SkipsConstructorOperatorAndExplicitInterface()
    {
        var type = CompileType("""
            public interface IWork
            {
                void DoWork();
            }

            public class Widget : IWork
            {
                public Widget() { }
                public static Widget operator +(Widget left, Widget right) => left;
                void IWork.DoWork() { }
            }
            """, "Widget");

        var existing = GenerateMethodStubOperation.ResolveMethodToReplace(
            type,
            "DoWork",
            Array.Empty<GenerateMethodStubOperation.InferredParameter>(),
            typeParameterCount: 0,
            replaceExisting: true);

        Assert.Null(existing);
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task GenerateMethodStub_VoidOnSameClass_AddsPrivateMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("DoWork", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Assignment_InfersReturnType()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int Compute()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Parameters_InfersArgumentTypesAndNames()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run(string name)
                {
                    Process(name, 42);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Process");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Process(string name, int arg2)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodOnOtherType_AddsPublicMethod()
    {
        const string source = """
            namespace TestApp;

            public class Caller
            {
                public void Run(Processor processor)
                {
                    processor.Transform();
                }
            }

            public class Processor
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Transform");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        Assert.Equal("Transform", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void Transform()", updated);
        Assert.Contains("class Processor", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticTypeCall_AddsStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    Helper.Compute(1);
                }
            }

            public class Helper
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static void Compute(int arg1)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThisReceiver_AddsPrivateMethodOnSameClass()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    this.DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("DoWork", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("stub will throw NotImplementedException", result.PendingChanges[0].Description);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ExplicitReturnType_OverridesInference()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "string"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_GenerateAsync_AddsAsyncTaskWithThrow()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            GenerateAsync = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private async Task DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_VisibilityOverride_EmitsRequestedModifier()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            Visibility = "internal"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("internal void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    #endregion

    #region ThrowNotImplemented

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedOmitted_UsesThrowBody()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        Assert.True(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        }.ThrowNotImplemented);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedTrue_UsesThrowBody()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_InferredVoid_EmptyBlock()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.DoesNotContain("return", ExtractMember(updated, "private void DoWork()"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_InferredReference_ReturnsNull()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    string value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string Compute()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_InferredValueType_ReturnsDefault()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int Compute()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_GenerateAsyncTask_EmptyBlockCompiles()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            GenerateAsync = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private async Task DoWork()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.DoesNotContain("return", ExtractMember(updated, "private async Task DoWork()"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_AwaitedTaskOfT_ReturnsDefaultAndCompiles()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Widget
            {
                public async Task Run()
                {
                    int value = await Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private async Task<int> Compute()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
        Assert.DoesNotContain("return null;", ExtractMember(updated, "private async Task<int> Compute()"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_GenerateAsyncTaskOfString_ReturnsNullAndCompiles()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Widget
            {
                public async Task Run()
                {
                    string value = await Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            GenerateAsync = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private async Task<string> Compute()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_InferredRefReturn_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    ref int value = ref GetCell();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetCell");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private ref int GetCell()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_InferredRefReadonlyReturn_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    ref readonly int value = ref GetOrigin();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetOrigin");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private ref readonly int GetOrigin()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_ExplicitRefReturnType_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    GetCell();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetCell");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "ref int",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private ref int GetCell()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_DoesNotWriteAndDescribesNonThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("stub will not throw", result.PendingChanges[0].Description);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("DoWork", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_OrdinaryValueReturn_DescribesNonThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will not throw", result.PendingChanges![0].Description);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("return default(int);", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_OrdinaryReferenceReturn_DescribesNonThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    string value = Compute();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Compute");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will not throw", result.PendingChanges![0].Description);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("return null;", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_InferredRefReturn_DescribesThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    ref int value = ref GetCell();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetCell");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will throw NotImplementedException", result.PendingChanges![0].Description);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("ref int GetCell()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_InferredRefReadonlyReturn_DescribesThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    ref readonly int value = ref GetOrigin();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetOrigin");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will throw NotImplementedException", result.PendingChanges![0].Description);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("ref readonly int GetOrigin()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_Preview_ExplicitRefReturn_DescribesThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    GetCell();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "GetCell");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "ref int",
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will throw NotImplementedException", result.PendingChanges![0].Description);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("ref int GetCell()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedTrue_Preview_DescribesThrowingStub()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ThrowNotImplemented = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("stub will throw NotImplementedException", result.PendingChanges![0].Description);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ThrowNotImplementedFalse_VisibilityAndGenerateAsync_StayAdditive()
    {
        const string source = """
            using System.Threading.Tasks;

            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            Visibility = "protected",
            GenerateAsync = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected async Task DoWork()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        AssertCompiles(updated);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task GenerateMethodStub_NoCallSite_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Widget");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodAlreadyExists_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }

                private void DoWork()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_CannotInferReturnType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    var value = Mystery();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Mystery");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.CannotInferReturnType, ex.ErrorCode);
        Assert.Contains("returnType", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ExternalTarget_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    "hello".Missing();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Missing");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
        Assert.Contains("external", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void GenerateMethodStub_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            GenerateMethodStubOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_CannotInferReturnType_ExplicitTypeSucceeds()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    var value = Mystery();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Mystery");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReturnType = "int"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private int Mystery()", updated);
    }

    #endregion

    #region Review Fold

    [SkippableFact]
    public async Task GenerateMethodStub_AlreadyResolvedImplicitConversion_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    M(1);
                }

                private void M(long value)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "M");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("already resolves", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_AlreadyResolvedOptionalParameter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    M();
                }

                private void M(int value = 0)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "M");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_RefParameter_PreservesRefKind()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = 1;
                    Process(ref value);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Process");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Process(ref int value)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_OutAndInParameters_PreserveRefKind()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = 1;
                    Combine(in value, out int result);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Combine");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Combine(in int value, out int result)", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_GenericInvocation_PreservesTypeParameters()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    Missing<int, string>();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Missing");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void Missing<T, T2>()", updated);
        Assert.Contains("Missing<int, string>();", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticConstructor_GeneratesStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                static Widget()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_StaticPropertyAccessor_GeneratesStaticMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public static int Value
                {
                    get
                    {
                        Init();
                        return 0;
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Init");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private static void Init()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodNameOverride_RewritesCallSite()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            MethodName = "Execute"
        });

        Assert.True(result.Success);
        Assert.Equal("Execute", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Execute();", updated);
        Assert.DoesNotContain("DoWork();", updated);
        Assert.Contains("private void Execute()", updated);
        Assert.DoesNotContain("private void DoWork()", updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_MethodNameOverride_PreviewRewritesCallSiteWithoutWriting()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            MethodName = "Execute",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.AfterSnippet?.Contains("Execute") == true);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting

    private const string WidgetWithDoWorkSource = """
        namespace TestApp;

        public class Widget
        {
            public void Run()
            {
                DoWork();
            }

            private void DoWork()
            {
                throw new System.InvalidOperationException("old");
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingOmitted_CompatibleMethod_ThrowsNameCollision()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithDoWorkSource);
        var (line, column) = FindIdentifier(WidgetWithDoWorkSource, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingFalse_CompatibleMethod_ThrowsNameCollision()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithDoWorkSource);
        var (line, column) = FindIdentifier(WidgetWithDoWorkSource, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column,
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_ExactMatch_ReplacesMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithDoWorkSource);
        var (line, column) = FindIdentifier(WidgetWithDoWorkSource, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old", updated);
        Assert.DoesNotContain("InvalidOperationException", updated);
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "void DoWork()"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_NoExistingMatch_GeneratesAsToday()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_DifferentArityAndParams_LeavesExistingAndGenerates()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork(42);
                }

                private void DoWork()
                {
                    throw new System.InvalidOperationException("keep-zero");
                }

                private void DoWork<T>(int value)
                {
                    throw new System.InvalidOperationException("keep-generic");
                }

                private void DoWork(string value)
                {
                    throw new System.InvalidOperationException("keep-string");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("keep-zero", updated);
        Assert.Contains("keep-generic", updated);
        Assert.Contains("keep-string", updated);
        Assert.Contains("private void DoWork(int", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_RefKindOverload_ReplacesOnlyMatchingRefKind()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    int value = 1;
                    Process(value);
                }

                private void Process(int value)
                {
                    throw new System.InvalidOperationException("old-byvalue");
                }

                private void Process(ref int value)
                {
                    throw new System.InvalidOperationException("keep-ref");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "Process");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-byvalue", updated);
        Assert.Contains("keep-ref", updated);
        Assert.Contains("private void Process(int value)", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_AmbiguousOverloads_FailsBeforeWrite()
    {
        const string source = """
            namespace TestApp;

            public class @int
            {
            }

            public class Widget
            {
                public void Run()
                {
                    DoWork(1);
                }

                private void DoWork(int value)
                {
                    throw new System.InvalidOperationException("old-int");
                }

                private void DoWork(@int value)
                {
                    throw new System.InvalidOperationException("old-class");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateMethodStubParams
            {
                SourceFile = workspace.SourcePath,
                Line = line,
                Column = column,
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("Multiple methods named 'DoWork'", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_ThrowNotImplementedFalse_EmitsDefaultReturnBody()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithDoWorkSource);
        var (line, column) = FindIdentifier(WidgetWithDoWorkSource, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("private void DoWork()", updated);
        var stub = ExtractMember(updated, "private void DoWork()");
        Assert.DoesNotContain("return", stub);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithDoWorkSource);
        var (line, column) = FindIdentifier(WidgetWithDoWorkSource, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Single(result.PendingChanges);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Replace method stub 'DoWork'", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing method 'DoWork'", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("private void DoWork()", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_Preview_NoExisting_IsSingleGenerateChange()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Single(result.PendingChanges);
        Assert.Contains("Generate method stub 'DoWork'", result.PendingChanges[0].Description);
        Assert.Contains("no method 'DoWork'", result.PendingChanges[0].BeforeSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_PartialOtherFile_RemovesThere_InsertsOnTarget()
    {
        const string callSitePart = """
            namespace TestApp;

            public partial class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        const string methodPart = """
            namespace TestApp;

            public partial class Widget
            {
                private void DoWork()
                {
                    throw new System.InvalidOperationException("old");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", callSitePart),
            ("Widget.Methods.cs", methodPart));
        var otherPath = workspace.PathFor("Widget.Methods.cs");
        var (line, column) = FindIdentifier(callSitePart, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        Assert.Contains("private void DoWork()", selected);
        Assert.Contains("throw new global::System.NotImplementedException();", selected);
        Assert.DoesNotContain("old", selected);
        Assert.DoesNotContain("void DoWork()", other);
        Assert.DoesNotContain("old", other);
        Assert.Equal(1, CountOccurrences(selected, "void DoWork()"));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string callSitePart = """
            namespace TestApp;

            public partial class Widget
            {
                public void Run()
                {
                    DoWork();
                }
            }
            """;

        const string methodPart = """
            namespace TestApp;

            public partial class Widget
            {
                private void DoWork()
                {
                    throw new System.InvalidOperationException("old");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", callSitePart),
            ("Widget.Methods.cs", methodPart));
        var otherPath = workspace.PathFor("Widget.Methods.cs");
        var (line, column) = FindIdentifier(callSitePart, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Replace method stub 'DoWork'", result.PendingChanges[0].Description);
        var otherChange = result.PendingChanges[1];
        Assert.Equal(otherPath, otherChange.File);
        Assert.Equal(RoslynMcp.Contracts.Enums.ChangeKind.Modify, otherChange.ChangeType);
        Assert.Contains("Remove existing method 'DoWork'", otherChange.Description);
        Assert.Contains("private void DoWork()", otherChange.BeforeSnippet);
        Assert.Contains("old", otherChange.BeforeSnippet);
        Assert.Equal("// method removed", otherChange.AfterSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_IfDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Run()
                {
                    DoWork();
                }

            #if DEBUG
                private void DoWork()
                {
                    throw new System.InvalidOperationException("old");
                }
            #endif

                public void Keep()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#if DEBUG", updated);
        Assert.Contains("#endif", updated);
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("public void Keep()", updated);
        Assert.DoesNotContain("old", updated);
        Assert.Equal(updated.Split("#if ").Length - 1, updated.Split("#endif").Length - 1);
        AssertCompiles(updated, preprocessorSymbols: "DEBUG");
    }

    [SkippableFact]
    public async Task GenerateMethodStub_ReplaceExistingTrue_DoesNotRemoveExplicitInterfaceImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface IWork
            {
                void DoWork();
            }

            public class Widget : IWork
            {
                public void Run()
                {
                    DoWork();
                }

                void IWork.DoWork()
                {
                    throw new System.InvalidOperationException("keep-explicit");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var (line, column) = FindIdentifier(source, "DoWork");
        var operation = new GenerateMethodStubOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateMethodStubParams
        {
            SourceFile = workspace.SourcePath,
            Line = line,
            Column = column,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("keep-explicit", updated);
        Assert.Contains("void IWork.DoWork()", updated);
        Assert.Contains("private void DoWork()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        AssertCompiles(updated);
    }

    #endregion

    #region Helpers

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStubMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static INamedTypeSymbol CompileType(string source, string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
                "GenerateMethodStubResolveTest",
                new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var decl = tree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == typeName);
        return model.GetDeclaredSymbol(decl) as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Could not resolve type '{typeName}'.");
    }

    private static void AssertCompiles(string source, params string[] preprocessorSymbols)
    {
        var parseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols(preprocessorSymbols);
        var compilation = CSharpCompilation.Create(
                "GenerateMethodStubCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Task).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated generate_method_stub did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

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

    private static string ExtractMember(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain '{signature}':\n{text}");
        var openBrace = text.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Generated member '{signature}' had no opening brace:\n{text}");
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Could not extract member '{signature}'.");
    }

    private static (int Line, int Column) FindIdentifier(string source, string name)
    {
        var normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = 0;
            while ((idx = lines[i].IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = idx == 0 || !SyntaxFacts.IsIdentifierPartCharacter(lines[i][idx - 1]);
                var after = idx + name.Length;
                var afterOk = after >= lines[i].Length || !SyntaxFacts.IsIdentifierPartCharacter(lines[i][after]);
                if (beforeOk && afterOk)
                    return (i + 1, idx + 1);

                idx += name.Length;
            }
        }

        throw new InvalidOperationException($"Identifier '{name}' not found in source.");
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateMethodStub_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Types.cs");

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

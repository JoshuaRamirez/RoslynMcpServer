using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Signature;

/// <summary>
/// Operation-level tests for <see cref="ChangeReturnTypeOperation"/>.
/// </summary>
public class ChangeReturnTypeOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewReturnType_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(newReturnType: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidTypeSyntax_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnInvalidType.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(ValidParams(sourceFile: path, newReturnType: "int int")));

            Assert.Equal(ErrorCodes.InvalidReturnType, ex.ErrorCode);
            Assert.Equal("1015", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidReturnType_RejectsInvalidSyntax()
    {
        Assert.False(ChangeReturnTypeOperation.IsValidReturnType("int int"));
        Assert.False(ChangeReturnTypeOperation.IsValidReturnType("@@@"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("int"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("void"));
        Assert.True(ChangeReturnTypeOperation.IsValidReturnType("List<string>"));
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = false,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrMethodName_DoesNotThrow()
    {
        ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithRelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = true,
                SourceFile = "Worker.cs",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNonCSharpSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = true,
                SourceFile = "/tmp/Worker.txt",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMissingSourceFile_DoesNotThrow()
    {
        ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
        {
            AllFiles = true,
            SourceFile = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeMissingAllFiles.cs"),
            NewReturnType = "long"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = true,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = true,
                Line = 1,
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
            {
                AllFiles = true,
                Column = 1,
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Change return type", ChangeReturnTypeOperation.BuildAllFilesDescription(1));
        Assert.Equal("Change 2 return types", ChangeReturnTypeOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ChangeReturnType_SimpleChange_UpdatesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public long Process()", text);
        Assert.Contains("return 1;", text);
        Assert.DoesNotContain("public int Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ImplicitConversion_LeavesReturnExpression()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public object Process()", text);
        Assert.Contains("return 1;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_NonVoidToVoid_StripsReturnExpressions()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "void"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process()", text);
        Assert.Contains("return;", text);
        Assert.DoesNotContain("return 1;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_VoidToNonVoid_AddsDefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                    System.Console.WriteLine("hi");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_VoidReturnToNonVoid_ReplacesBareReturn()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(bool skip)
                {
                    if (skip)
                        return;
                    System.Console.WriteLine("go");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process(bool skip)", text);
        Assert.Contains("return default(int);", text);
        Assert.DoesNotContain("\n            return;", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process()
                {
                    return 1;
                }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 2;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object",
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("object Process();", text);
        Assert.Contains("public virtual object Process()", text);
        Assert.Contains("public override object Process()", text);
        Assert.Contains("return 1;", text);
        Assert.Contains("return 2;", text);
        Assert.DoesNotContain("int Process()", text);
        Assert.DoesNotContain("int Process();", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public long Process()") &&
            c.AfterSnippet.Contains("return 1;"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ExpressionBodiedVoidToValue_AddsDefault()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process() => System.Console.WriteLine("hi");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ChangeReturnType_SameType_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Incompatible_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public string Process()
                {
                    return "hi";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal("3133", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_OverloadCollision_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public string Process()
                {
                    return "a";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "string",
                Line = 5
            }));

        Assert.Equal(ErrorCodes.SignatureMatchesOverload, ex.ErrorCode);
        Assert.Equal("3132", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    System.Func<int> fn = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "object"
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Other() => 1;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ChangeReturnType_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ConvertDisabledVoidToValue_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int",
                ConvertReturnStatements = false
            }));

        Assert.Equal(ErrorCodes.CannotConvertReturn, ex.ErrorCode);
        Assert.Equal("3134", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_InvocationResultContext_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    int value = Process();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_DiscardedInvocation_AllowsWidening()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process()
                {
                    return 1;
                }

                public void Run()
                {
                    Process();
                    var inferred = Process();
                    object boxed = Process();
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public long Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_UneditableOverrideContract_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public override int GetHashCode()
                {
                    return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "GetHashCode",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_UneditableInterfaceContract_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker : System.IDisposable
            {
                public void Dispose()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Dispose",
                NewReturnType = "int"
            }));

        Assert.Equal(ErrorCodes.ReturnTypeIncompatible, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AsyncTask_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public async System.Threading.Tasks.Task Process()
                {
                    return;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "System.Threading.Tasks.Task<int>"
            }));

        Assert.Equal(ErrorCodes.AsyncReturnTypeUnsupported, ex.ErrorCode);
        Assert.Equal("3135", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Iterator_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public System.Collections.Generic.IEnumerable<int> Process()
                {
                    yield return 1;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "System.Collections.Generic.IEnumerable<string>"
            }));

        Assert.Equal(ErrorCodes.ContainsYield, ex.ErrorCode);
        Assert.Equal("3031", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_QualifiesTypeInOtherDocument()
    {
        const string worker = """
            using Text = System.String;

            namespace TestApp;

            public class Worker
            {
                public virtual object Process()
                {
                    return "";
                }
            }
            """;
        const string derived = """
            namespace TestApp;

            public class Derived : Worker
            {
                public override object Process()
                {
                    return "";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Worker.cs", worker),
            ("Derived.cs", derived));
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "Text",
            Line = 7
        });

        Assert.True(result.Success);

        var workerText = await File.ReadAllTextAsync(workspace.SourcePath);
        var derivedText = await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Derived.cs"));
        Assert.Contains("virtual Text Process()", workerText);
        Assert.DoesNotContain("Text Process()", derivedText);
        Assert.True(
            derivedText.Contains("override string Process()") ||
            derivedText.Contains("override System.String Process()"),
            "Derived file should use a context-valid string type, not the originating alias.\n" + derivedText);
    }

    #endregion

    #region Covering-span column

    private const string SameLineOverloadsSource = """
        namespace TestApp;

        public class Worker
        {
            public int Process(int x) { return x; } public int Process(int x, int y) { return x + y; }
        }
        """;

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeInvalidColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewReturnType = "long",
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
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeNegativeColumn.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ChangeReturnTypeOperation.Validate(new ChangeReturnTypeParams
                {
                    SourceFile = path,
                    MethodName = "Process",
                    NewReturnType = "long",
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
    public void FindMethod_ColumnPicksIdentifierCoverage()
    {
        var tree = CSharpSyntaxTree.ParseText(SameLineOverloadsSource);
        var root = tree.GetRoot();
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var first = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x) { return x; }"));
        var second = FindMethodHelpers.FindMethod(
            root, "Process", line, ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }"));
        var omitted = FindMethodHelpers.FindMethod(root, "Process", line, column: null);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(omitted);
        Assert.Equal(["x"], ParameterNames(first));
        Assert.Equal(["x", "y"], ParameterNames(second));
    }

    [Fact]
    public void FindMethod_ColumnOnContinuationLine_PicksMethod()
    {
        const string source = """
            class C
            {
                public int
                Process(int x) { return x; }

                public int Process(int x, int y) { return x + y; }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var startLine = FindLine(source, "public int");
        var identifierLine = FindLine(source, "Process(int x) { return x; }");
        Assert.NotEqual(startLine, identifierLine);

        // Omitted column keeps today's start-line filter — the split
        // signature does not start on the identifier line. Column still
        // selects it.
        var byStartLineOnly = FindMethodHelpers.FindMethod(root, "Process", identifierLine, column: null);
        var byColumn = FindMethodHelpers.FindMethod(
            root, "Process", identifierLine, ColumnOf(source, "Process(int x) { return x; }"));

        Assert.Null(byStartLineOnly);
        Assert.NotNull(byColumn);
        Assert.Equal(["x"], ParameterNames(byColumn));
    }

    [Fact]
    public void FindMethod_AdjacentMethods_ExclusiveEndDoesNotStealNextMethod()
    {
        const string source = """
            class C
            {
                public int Other(int x){return x;}public int Process(int x){return x;}
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var line = FindLine(source, "public int Other");
        var secondStart = ColumnOf(source, "public int Process");
        var secondId = ColumnOf(source, "Process(int x){return x;}");

        var atSecondStart = FindMethodHelpers.FindMethod(root, "Process", line, secondStart);
        var atSecondId = FindMethodHelpers.FindMethod(root, "Process", line, secondId);
        var atFirstId = FindMethodHelpers.FindMethod(root, "Other", line, ColumnOf(source, "Other(int x)"));
        var firstAtSecondStart = FindMethodHelpers.FindMethod(root, "Other", line, secondStart);

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
        const string source = "class C { public int A(int x){return x;}public int B(int x){return x;} }";
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

    [SkippableFact]
    public async Task ChangeReturnType_OmittedColumn_SameLineOverloads_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_SelectsSecondOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = secondColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "long");
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_SelectsFirstOverloadOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var firstColumn = ColumnOf(SameLineOverloadsSource, "Process(int x) { return x; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = firstColumn
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "long");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
        Assert.DoesNotContain(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
    }

    [SkippableFact]
    public async Task ChangeReturnType_ColumnOnContinuationLine_ChangesThatMethod()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public int
                Process(int x) { return x; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "Process(int x) { return x; }"),
            Column = ColumnOf(source, "Process(int x) { return x; }")
        });

        Assert.True(result.Success);
        var updated = (await File.ReadAllTextAsync(workspace.SourcePath)).Replace("\r\n", "\n");
        Assert.Contains("public long", updated);
        Assert.Contains("Process(int x)", updated);
        Assert.DoesNotContain("public int\n                Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_OmittedColumn_ContinuationLineIdentifier_ThrowsMethodNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Split
            {
                public int
                Process(int x) { return x; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "long",
                Line = FindLine(source, "Process(int x) { return x; }")
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AdjacentMethods_ColumnOnSecondDoesNotRewriteFirst()
    {
        const string source = """
            namespace TestApp;

            public class Adjacent
            {
                public int Other(int x){return x;}public int Process(int x){return x;}
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "public int Other"),
            Column = ColumnOf(source, "Process(int x){return x;}")
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Other(int x)", updated);
        Assert.Contains("public long Process(int x)", updated);
        Assert.DoesNotContain("public long Other", updated);
        Assert.DoesNotContain("public int Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_ColumnWithoutLine_SameIndentOverloads_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Foo(int x)
                {
                    return x;
                }

                public int Foo(int x, int y)
                {
                    return x + y;
                }
            }
            """;

        var column = ColumnOf(source, "Foo(int x)");
        Assert.Equal(column, ColumnOf(source, "Foo(int x, int y)"));

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Foo",
                NewReturnType = "long",
                Column = column
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineOverloadsSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var line = FindLine(SameLineOverloadsSource, "public int Process(int x) { return x; }");
        var secondColumn = ColumnOf(SameLineOverloadsSource, "Process(int x, int y) { return x + y; }");

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = line,
            Column = secondColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.AfterSnippet != null &&
            change.AfterSnippet.Contains("long Process(int x, int y)", StringComparison.Ordinal) &&
            change.AfterSnippet.Contains("int Process(int x)", StringComparison.Ordinal) &&
            !change.AfterSnippet.Contains("int Process(int x, int y)", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_UpdateOverridesAndImplementations_StillUpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process() { return 1; } public int Process(string name) { return 2; }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 3;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "object",
            Line = FindLine(source, "public virtual int Process"),
            Column = ColumnOf(source, "Process() { return 1; }"),
            UpdateOverrides = true,
            UpdateImplementations = true
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("object Process();", text);
        Assert.Contains("public virtual object Process()", text);
        Assert.Contains("public override object Process()", text);
        Assert.Contains("public int Process(string name)", text);
        Assert.Contains("return 1;", text);
        Assert.Contains("return 2;", text);
        Assert.Contains("return 3;", text);
        Assert.DoesNotContain("int Process();", text);
        Assert.DoesNotContain("virtual int Process()", text);
        Assert.DoesNotContain("override int Process()", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_UpdateOverridesAndImplementationsFalse_OnlySelectedMethod()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                int Process();
            }

            public class Worker : IWorker
            {
                public virtual int Process() { return 1; } public int Process(string name) { return 2; }
            }

            public class Derived : Worker
            {
                public override int Process()
                {
                    return 3;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        // Column picks the non-virtual same-line sibling so false flags stay
        // compiling: only that declaration changes. The virtual / interface /
        // override chain is left alone — same rewrite rules as today.
        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long",
            Line = FindLine(source, "public virtual int Process"),
            Column = ColumnOf(source, "Process(string name) { return 2; }"),
            UpdateOverrides = false,
            UpdateImplementations = false
        });

        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("int Process();", text);
        Assert.Contains("public virtual int Process()", text);
        Assert.Contains("public override int Process()", text);
        Assert.Contains("public long Process(string name)", text);
        Assert.DoesNotContain("public int Process(string name)", text);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_ConvertReturnStatements_StillConvertsSelected()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int x) { return x; } public void Process(int x, int y) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "int",
            Line = FindLine(source, "public int Process(int x)"),
            Column = ColumnOf(source, "Process(int x, int y)"),
            ConvertReturnStatements = true
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var processMethods = GetMethods(updated, "Process");
        Assert.Equal(2, processMethods.Count);
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x"] && ReturnTypeText(m) == "int");
        Assert.Contains(processMethods, m => ParameterNames(m) is ["x", "y"] && ReturnTypeText(m) == "int");
        Assert.Contains("return default(int);", updated.Replace("\r\n", "\n"));
        Assert.DoesNotContain("void Process", updated);
    }

    [SkippableFact]
    public async Task ChangeReturnType_Column_ConvertReturnStatementsFalse_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int x) { return x; } public void Process(int x, int y) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewReturnType = "int",
                Line = FindLine(source, "public int Process(int x)"),
                Column = ColumnOf(source, "Process(int x, int y)"),
                ConvertReturnStatements = false
            }));

        Assert.Equal(ErrorCodes.CannotConvertReturn, ex.ErrorCode);
        Assert.Equal("3134", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion


    #region allFiles

    private const string EligibleFileA = """
        public class FileA
        {
            public int Process() => 1;
            public int Other() => 2;
            public string KeepString() => "x";
        }
        """;

    private const string EligibleFileB = """
        public class FileB
        {
            public int Process() => 3;
        }
        """;

    private const string IneligibleFileC = """
        using System.Threading.Tasks;
        public class FileC
        {
            public async Task<int> Bad() => 1;
            public long AlreadyLong() => 1L;
        }
        """;

    [SkippableFact]
    public async Task ChangeReturnType_OmittedAllFiles_KeepsSingleSiteRewrite()
    {
        const string source = """
            public class Worker
            {
                public int Process() => 1;
                public int Other() => 2;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal("long", ReturnTypeText(GetMethods(updated, "Process").Single()));
        Assert.Equal("int", ReturnTypeText(GetMethods(updated, "Other").Single()));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_AppliesToEligibleMethodsAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = await File.ReadAllTextAsync(pathA);
        var updatedB = await File.ReadAllTextAsync(pathB);
        Assert.Equal("long", ReturnTypeText(GetMethods(updatedA, "Process").Single()));
        Assert.Equal("long", ReturnTypeText(GetMethods(updatedA, "Other").Single()));
        Assert.Equal("string", ReturnTypeText(GetMethods(updatedA, "KeepString").Single()));
        Assert.Equal("long", ReturnTypeText(GetMethods(updatedB, "Process").Single()));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathA));
        Assert.Contains(result.Changes.FilesModified, p => PathsEqual(p, pathB));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathC));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_WithoutSourceFileOrMethodName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesModified.Count >= 2);
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                AllFiles = false,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_WithMethodName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ChangeReturnTypeParams
            {
                AllFiles = true,
                MethodName = "Process",
                NewReturnType = "long"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("methodName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ChangeReturnType_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var pathC = Path.Combine(workspace.DirectoryPath, "FileC.cs");
        var beforeA = await File.ReadAllTextAsync(pathA);
        var beforeB = await File.ReadAllTextAsync(pathB);
        var beforeC = await File.ReadAllTextAsync(pathC);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathA));
        Assert.Contains(result.PendingChanges, c => PathsEqual(c.File, pathB));
        Assert.DoesNotContain(result.PendingChanges, c => PathsEqual(c.File, pathC));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(pathA));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(pathC));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC));
        var operation = new ChangeReturnTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var pathA = Path.Combine(workspace.DirectoryPath, "FileA.cs");
        var pathB = Path.Combine(workspace.DirectoryPath, "FileB.cs");
        var beforeB = await File.ReadAllTextAsync(pathB);

        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            SourceFile = pathA,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        var updatedA = await File.ReadAllTextAsync(pathA);
        Assert.Equal("long", ReturnTypeText(GetMethods(updatedA, "Process").Single()));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(pathB));
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, pathA));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathsEqual(p, pathB));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_LinkedDocument_CoalescesIdenticalRewrites()
    {
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
                public static int Process() => 1;
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
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathsEqual(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.Contains(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["Shared.cs"]));
        var updated = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        Assert.Equal("long", ReturnTypeText(GetMethods(updated, "Process").Single()));

        var texts = new List<string>();
        foreach (var document in linkedDocuments)
        {
            var current = workspace.Context.Solution.GetDocument(document.Id);
            Assert.NotNull(current);
            texts.Add((await current!.GetTextAsync()).ToString());
        }

        Assert.Equal(2, texts.Count);
        Assert.Equal(texts[0], texts[1], StringComparer.Ordinal);
        Assert.Equal("long", ReturnTypeText(GetMethods(texts[0], "Process").Single()));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_LinkedDocument_SkipsWhenSiblingCannotHonor()
    {
        // Shared physical file linked into two projects with different defines.
        // ProjectA sees int Process(); ProjectB already sees long Process() via
        // #else — primary could rewrite int→long, but sibling TryChangeOne is a
        // no-op (TypesEquivalent), so LinkedViewsCanHonor must skip coalesce.
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
            #if PROJ_A
                public static int Process() => 1;
            #else
                public static long Process() => 1L;
            #endif
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
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource,
            projectADefineConstants: "PROJ_A",
            projectBDefineConstants: "PROJ_B");
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathsEqual(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var before = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "long"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.DoesNotContain(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["Shared.cs"]));
    }

    [SkippableFact]
    public async Task ChangeReturnType_AllFilesTrue_LinkedDocument_SkipsWhenSiblingCallerIncompatible()
    {
        // Caller lives only in ProjectB's compilation and binds the sibling
        // IMethodSymbol. FindReferences on the primary linked symbol misses it;
        // sibling rematch must reject changing int→string.
        const string sharedSource = """
            namespace TestApp;

            public static class Shared
            {
                public static int Process() => 1;
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
                    int x = Shared.Process();
                    return x;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathsEqual(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var before = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        var operation = new ChangeReturnTypeOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new ChangeReturnTypeParams
        {
            AllFiles = true,
            NewReturnType = "string"
        });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]));
        Assert.DoesNotContain(result.Changes!.FilesModified, p => PathsEqual(p, workspace.SourcePaths["Shared.cs"]));
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Helpers

    private static List<MethodDeclarationSyntax> GetMethods(string source, string methodName) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

    private static string[] ParameterNames(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Select(p => p.Identifier.Text).ToArray();

    private static string ReturnTypeText(MethodDeclarationSyntax method) =>
        method.ReturnType.ToString();

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

    private static ChangeReturnTypeParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        string newReturnType = "long") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeMissing.cs"),
            MethodName = methodName,
            NewReturnType = newReturnType
        };

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public Dictionary<string, string> SourcePaths { get; init; } = new(StringComparer.Ordinal);
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnType_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
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

            sourcePath ??= Path.Combine(directory, "Worker.cs");

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

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource,
            string? projectADefineConstants = null,
            string? projectBDefineConstants = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpChangeReturnTypeLinked_" + Guid.NewGuid().ToString("N"));
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

            string ProjectXml(string anchorFile, string? defineConstants)
            {
                var defineLine = string.IsNullOrWhiteSpace(defineConstants)
                    ? ""
                    : $"\n                    <DefineConstants>{defineConstants}</DefineConstants>";
                return $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>{defineLine}
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{anchorFile}" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """;
            }

            await File.WriteAllTextAsync(rootProjectPath, ProjectXml("AnchorA.cs", projectADefineConstants));
            await File.WriteAllTextAsync(referencedProjectPath, ProjectXml("AnchorB.cs", projectBDefineConstants));

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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Signature;
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
                NewReturnType = "string"
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
            ChangeReturnTypeOperation.ValidateDocumentIsEditable(document, workspace));

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

    #endregion

    #region Helpers

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
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs")
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

            var sourcePath = Path.Combine(directory, fileName);
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

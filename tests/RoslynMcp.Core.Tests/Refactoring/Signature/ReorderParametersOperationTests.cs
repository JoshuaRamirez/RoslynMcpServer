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
/// Operation-level tests for <see cref="ReorderParametersOperation"/>.
/// </summary>
public class ReorderParametersOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingNewOrder_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(newOrder: Array.Empty<int>())));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidPermutation_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderInvalidPerm.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                ReorderParametersOperation.Validate(ValidParams(sourceFile: path, newOrder: new[] { 0, 0 })));

            Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
            Assert.Equal("1011", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidateNewOrder_LengthMismatch_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.ValidateNewOrder(3, new[] { 1, 0 }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task ReorderParameters_SimpleSwap_ReordersDeclarationAndCallSite()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(3, "a");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(string name, int count)", text);
        Assert.Contains("Process(\"a\", 3)", text);
        Assert.DoesNotContain("Process(3, \"a\")", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_NamedArgs_LeftInPlace()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name, bool flag)
                {
                    System.Console.WriteLine(count + name + flag);
                }

                public void Run()
                {
                    Process(count: 3, name: "a", flag: false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 2, 0, 1 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(bool flag, int count, string name)", text);
        Assert.Contains("Process(count: 3, name: \"a\", flag: false)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_MixedNamedAndPositional_ReordersPositionalLeavesNamed()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(3, name: "a");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(string name, int count)", text);
        Assert.Contains("Process(name: \"a\", 3)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, string name);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, string name)
                {
                }
            }

            public class Derived : Worker
            {
                public override void Process(int count, string name)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived)
                {
                    worker.Process(1, "a");
                    derived.Process(2, "b");
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(string name, int count);", text);
        Assert.Contains("public virtual void Process(string name, int count)", text);
        Assert.Contains("public override void Process(string name, int count)", text);
        Assert.Contains("worker.Process(\"a\", 1)", text);
        Assert.Contains("derived.Process(\"b\", 2)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_ReducedExtensionCall_OffsetsThis()
    {
        const string source = """
            namespace TestApp;

            public static class Exts
            {
                public static void Ext(this string value, int first, int second)
                {
                }
            }

            public class Worker
            {
                public void Run()
                {
                    var text = "hi";
                    text.Ext(1, 2);
                    Exts.Ext(text, 1, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Ext",
            NewOrder = new[] { 0, 2, 1 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public static void Ext(this string value, int second, int first)", text);
        Assert.Contains("text.Ext(2, 1)", text);
        Assert.Contains("Exts.Ext(text, 2, 1)", text);
        Assert.DoesNotContain("text.Ext(1, 2)", text);
        Assert.DoesNotContain("Exts.Ext(text, 1, 2)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_EscapedNamedArg_UsesValueText()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, int @class)
                {
                    System.Console.WriteLine(count + @class);
                }

                public void Run()
                {
                    Process(count: 3, @class: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int @class, int count)", text);
        Assert.Contains("Process(count: 3, @class: 1)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_PreservesSurvivingSeparatorTrivia()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int a, // explanation
                    int b, int c)
                {
                    System.Console.WriteLine(a + b + c);
                }

                public void Run()
                {
                    Process(1, 2, 3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 0, 1, 2 }
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("explanation", text);
        Assert.Contains("public void Process(int a, // explanation", text);
        Assert.Contains("Process(1, 2, 3)", text);
    }

    [SkippableFact]
    public async Task ReorderParameters_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }

                public void Run() => Process(3, "a");
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ReorderParametersParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            NewOrder = new[] { 1, 0 },
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public void Process(string name, int count)") &&
            c.AfterSnippet.Contains("Process(\"a\", 3)"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task ReorderParameters_InvalidPermutation_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 0, 2 }
            }));

        Assert.Equal(ErrorCodes.InvalidParameterPosition, ex.ErrorCode);
        Assert.Equal("1011", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_ParamsNotLast_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, params int[] values)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.ParamsNotLast, ex.ErrorCode);
        Assert.Equal("3129", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_OptionalBeforeRequired_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name = "a")
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.RequiredAfterOptional, ex.ErrorCode);
        Assert.Equal("3128", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int count, string name) => 0;

                public void Run()
                {
                    System.Func<int, string, int> handler = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal("3130", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ReorderParameters_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ReorderParametersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ReorderParametersParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "DoesNotExist",
                NewOrder = new[] { 1, 0 }
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ReorderParameters_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ReorderParametersOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static ReorderParametersParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        int[]? newOrder = null) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParametersMissing.cs"),
            MethodName = methodName,
            NewOrder = newOrder ?? new[] { 1, 0 }
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpReorderParameters_" + Guid.NewGuid().ToString("N"));
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

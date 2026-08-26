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
/// Operation-level tests for <see cref="RemoveParameterOperation"/>.
/// </summary>
public class RemoveParameterOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingMethodName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(methodName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingParameterName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(parameterName: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams(sourceFile: "Worker.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task RemoveParameter_UnusedParam_RemovesDeclarationAndCallSiteArg()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, bool unused)
                {
                    System.Console.WriteLine(count);
                }

                public void Run()
                {
                    Process(3, false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count)", text);
        Assert.DoesNotContain("bool unused", text);
        Assert.Contains("Process(3)", text);
        Assert.DoesNotContain("Process(3, false)", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_NamedArgs_RemovesNamedArgument()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, string name, bool unused)
                {
                    System.Console.WriteLine(count + name);
                }

                public void Run()
                {
                    Process(count: 3, name: "a", unused: false);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count, string name)", text);
        Assert.Contains("Process(count: 3, name: \"a\")", text);
        Assert.DoesNotContain("unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_OverrideAndInterface_UpdatesChain()
    {
        const string source = """
            namespace TestApp;

            public interface IWorker
            {
                void Process(int count, bool unused);
            }

            public class Worker : IWorker
            {
                public virtual void Process(int count, bool unused)
                {
                }
            }

            public class Derived : Worker
            {
                public override void Process(int count, bool unused)
                {
                }
            }

            public static class Runner
            {
                public static void Run(IWorker worker, Derived derived)
                {
                    worker.Process(1, false);
                    derived.Process(2, true);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Line = 10
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("void Process(int count);", text);
        Assert.Contains("public virtual void Process(int count)", text);
        Assert.Contains("public override void Process(int count)", text);
        Assert.Contains("worker.Process(1)", text);
        Assert.Contains("derived.Process(2)", text);
        Assert.DoesNotContain("bool unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, bool unused)
                {
                }

                public void Run() => Process(3, false);
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.AfterSnippet != null &&
            c.AfterSnippet.Contains("public void Process(int count)") &&
            c.AfterSnippet.Contains("Process(3)") &&
            !c.AfterSnippet.Contains("bool unused"));

        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(original, after);
    }

    [SkippableFact]
    public async Task RemoveParameter_ForceTrue_ReplacesBodyUsages()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    return unused;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Force = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("return default(int);", text);
        Assert.DoesNotContain("int unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_ForceTrue_VarCopy_UsesTypedDefault()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    var copy = unused;
                    return copy;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused",
            Force = true
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Process()", text);
        Assert.Contains("var copy = default(int);", text);
        Assert.DoesNotContain("unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_ReducedExtensionCall_DropsExplicitArg()
    {
        const string source = """
            namespace TestApp;

            public static class Exts
            {
                public static void Ext(this string value, int unused)
                {
                }
            }

            public class Worker
            {
                public void Run()
                {
                    var text = "hi";
                    text.Ext(1);
                    Exts.Ext(text, 2);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Ext",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public static void Ext(this string value)", text);
        Assert.Contains("text.Ext();", text);
        Assert.Contains("Exts.Ext(text);", text);
        Assert.DoesNotContain("text.Ext(1)", text);
        Assert.DoesNotContain("Exts.Ext(text, 2)", text);
        Assert.DoesNotContain("int unused", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_EscapedNamedArg_UsesValueText()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count, int @class)
                {
                    System.Console.WriteLine(count);
                }

                public void Run()
                {
                    Process(count: 3, @class: 1);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "@class"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public void Process(int count)", text);
        Assert.Contains("Process(count: 3)", text);
        Assert.DoesNotContain("@class", text);
    }

    [SkippableFact]
    public async Task RemoveParameter_PreservesSurvivingSeparatorTrivia()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int a, // explanation
                    int b, int unused)
                {
                    System.Console.WriteLine(a + b);
                }

                public void Run()
                {
                    Process(1, 2, 3);
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveParameterParams
        {
            SourceFile = workspace.SourcePath,
            MethodName = "Process",
            ParameterName = "unused"
        });

        Assert.True(result.Success);

        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("explanation", text);
        Assert.Contains("public void Process(int a, // explanation", text);
        Assert.Contains("Process(1, 2)", text);
        Assert.DoesNotContain("int unused", text);
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task RemoveParameter_UsedInBody_ForceFalse_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused)
                {
                    return unused;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.ParameterUsedInBody, ex.ErrorCode);
        Assert.Equal("3131", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_MethodGroup_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public int Process(int unused) => 0;

                public void Run()
                {
                    System.Func<int, int> handler = Process;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "unused"
            }));

        Assert.Equal(ErrorCodes.UnsupportedCallSite, ex.ErrorCode);
        Assert.Equal("3130", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RemoveParameter_ParameterNotFound_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "Process",
                ParameterName = "missing"
            }));

        Assert.Equal(ErrorCodes.ParameterNotFound, ex.ErrorCode);
        Assert.Equal("2016", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveParameter_MissingMethod_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Worker
            {
                public void Process(int count)
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new RemoveParameterOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveParameterParams
            {
                SourceFile = workspace.SourcePath,
                MethodName = "DoesNotExist",
                ParameterName = "count"
            }));

        Assert.Equal(ErrorCodes.MethodNotFound, ex.ErrorCode);
    }

    [Fact]
    public void RemoveParameter_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            RemoveParameterOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers

    private static RemoveParameterParams ValidParams(
        string? sourceFile = null,
        string methodName = "Process",
        string parameterName = "unused") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameterMissing.cs"),
            MethodName = methodName,
            ParameterName = parameterName
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveParameter_" + Guid.NewGuid().ToString("N"));
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

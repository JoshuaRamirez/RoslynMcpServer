using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractInterfaceOperation"/>, including <c>separateFile</c>.
/// </summary>
public class ExtractInterfaceOperationTests
{
    private const string CalculatorSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Multiply(int a, int b) => a * b;
        }
        """;

    [SkippableFact]
    public async Task ExtractInterface_Default_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        Assert.Contains("Calculator : ICalculator", updated);
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileFalse_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        Assert.Contains("Calculator : ICalculator", updated);
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_WritesSiblingFileAndRemovesInterfaceFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var interfaceFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("interface ICalculator", source);
        Assert.Contains("Calculator : ICalculator", source);
        Assert.Contains("interface ICalculator", interfaceFile);
        Assert.Contains("int Add(int a, int b);", interfaceFile);
        Assert.Contains("int Multiply(int a, int b);", interfaceFile);
    }

    [SkippableFact]
    public async Task ExtractInterface_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomInterface.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            TargetFile = explicitTarget
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(explicitTarget));
        Assert.False(File.Exists(sibling));
        Assert.Contains(explicitTarget, result.Changes!.FilesCreated);
        Assert.DoesNotContain(sibling, result.Changes.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var custom = NormalizeNewlines(await File.ReadAllTextAsync(explicitTarget));

        Assert.DoesNotContain("interface ICalculator", source);
        Assert.Contains("interface ICalculator", custom);
        Assert.Contains("Calculator : ICalculator", source);
    }

    [SkippableFact]
    public async Task ExtractInterface_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("ICalculator", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public interface IExisting
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Calculator",
                InterfaceName = "ICalculator",
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractInterface_" + Guid.NewGuid().ToString("N"));
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
}

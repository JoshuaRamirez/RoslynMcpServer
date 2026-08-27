using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractBaseClassOperation"/>, including <c>separateFile</c>.
/// </summary>
public class ExtractBaseClassOperationTests
{
    private const string EmployeeSource = """
        namespace TestApp;

        public class Employee
        {
            public string Name { get; set; }

            public int Age { get; set; }

            public void Work() { }
        }
        """;

    [SkippableFact]
    public async Task ExtractBaseClass_Default_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileFalse_WritesBaseClassIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated);
        AssertInheritsFrom(updated, "Employee", "Person");
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_WritesSiblingFileAndRemovesBaseClassFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var baseFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("class Person", source);
        AssertInheritsFrom(source, "Employee", "Person");
        Assert.Contains("class Person", baseFile);
        Assert.Contains("Name", baseFile);
        Assert.Contains("Age", baseFile);
    }

    [SkippableFact]
    public async Task ExtractBaseClass_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomBase.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
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

        Assert.DoesNotContain("class Person", source);
        Assert.Contains("class Person", custom);
        AssertInheritsFrom(source, "Employee", "Person");
    }

    [SkippableFact]
    public async Task ExtractBaseClass_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
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
    public async Task ExtractBaseClass_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var operation = new ExtractBaseClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));

        var result = await operation.ExecuteAsync(new ExtractBaseClassParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Employee",
            BaseClassName = "Person",
            Members = new[] { "Name", "Age" },
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("Person", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractBaseClass_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EmployeeSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "Person.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public class Existing
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractBaseClassOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractBaseClassParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Employee",
                BaseClassName = "Person",
                Members = new[] { "Name", "Age" },
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    /// <summary>
    /// Base-list trivia from <c>WithBaseList</c> may omit spaces; compare a compacted form.
    /// </summary>
    private static void AssertInheritsFrom(string source, string typeName, string baseClassName)
    {
        var compact = new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains($"class{typeName}:{baseClassName}", compact);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Employee.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractBaseClass_" + Guid.NewGuid().ToString("N"));
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

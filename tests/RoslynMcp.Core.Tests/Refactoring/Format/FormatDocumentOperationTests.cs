using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Format;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Format;

/// <summary>
/// Operation-level tests for <see cref="FormatDocumentOperation"/>, including <c>preview</c> and <c>allFiles</c>.
/// </summary>
public class FormatDocumentOperationTests
{
    private const string UnformattedSource = """
        using System;
        namespace TestApp{
        public class Foo{
        public void Bar(){
        var x=1+2;
        if(x>0){
        Console.WriteLine(x);
        }
        }
        }
        }
        """;

    [SkippableFact]
    public async Task FormatDocument_OmittedPreview_WritesFormattedFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.NotEqual(before, after);
        Assert.Contains("var x = 1 + 2;", after);
    }

    [SkippableFact]
    public async Task FormatDocument_PreviewFalse_WritesFormattedFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.NotEqual(before, after);
        Assert.Contains("var x = 1 + 2;", after);
    }

    [SkippableFact]
    public async Task FormatDocument_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Modify, result.PendingChanges[0].ChangeType);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("var x=1+2;", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("var x = 1 + 2;", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task FormatDocument_Preview_AlreadyFormatted_SucceedsWithoutWriting()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var apply = new FormatDocumentOperation(workspace.Context);
        var applyResult = await apply.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(applyResult.Success);

        var formatted = await File.ReadAllTextAsync(workspace.SourcePath);
        var preview = new FormatDocumentOperation(workspace.Context);
        var result = await preview.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Empty(result.PendingChanges);
        Assert.Equal(formatted, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesFalse_FormatsOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["UnformattedA.cs"],
            AllFiles = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_WithoutSourceFile_FormatsMultipleUnformattedFiles_LeavesAlreadyFormattedUntouched()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        var afterB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Contains("var b = 3 + 4;", afterB);
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AlreadyFormatted.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FormatDocumentParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FormatDocument_PreviewAllFiles_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnformattedB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
    }

    private const string UnformattedA = """
        using System;
        namespace TestApp{
        public class UnformattedA{
        public void Bar(){
        var a=1+2;
        }
        }
        }
        """;

    private const string UnformattedB = """
        using System;
        namespace TestApp{
        public class UnformattedB{
        public void Bar(){
        var b=3+4;
        }
        }
        }
        """;

    private const string AlreadyFormatted = """
        using System;

        namespace TestApp
        {
            public class AlreadyFormatted
            {
                public void Bar()
                {
                    var x = 1 + 2;
                }
            }
        }
        """;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFormatDocument_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
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
                    SourcePath = sourcePaths.Values.First(),
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
}

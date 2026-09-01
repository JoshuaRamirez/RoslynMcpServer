using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Format;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Format;

/// <summary>
/// Operation-level tests for <see cref="FormatDocumentOperation"/>, including <c>preview</c>.
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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFormatDocument_" + Guid.NewGuid().ToString("N"));
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

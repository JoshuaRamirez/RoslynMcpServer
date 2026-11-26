using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Regression tests for #3088: the single-file preview of add_missing_usings / remove_unused_usings
/// reported UsingDirectivesAdded / UsingDirectivesRemoved = 0, while the allFiles preview and apply
/// reported the real count.
/// </summary>
public class UsingsPreviewCountTests
{
    private const string MissingSource = """
        namespace TestApp
        {
            public class MissingA
            {
                public void M()
                {
                    List<int> items = new List<int>();
                }
            }
        }
        """;

    private const string UnusedSource = """
        using System;
        using System.Text;
        using System.Linq;
        namespace TestApp
        {
            public class C { public void M() { Console.WriteLine(1); } }
        }
        """;

    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task AddMissingUsings_CountIsTheSameForPreviewAllFilesAndApply(bool allFiles, bool preview)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingSource, "latest");

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles,
            Preview = preview
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
    }

    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RemoveUnusedUsings_CountIsTheSameForPreviewAllFilesAndApply(bool allFiles, bool preview)
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnusedSource, "latest");

        var result = await new RemoveUnusedUsingsOperation(workspace.Context).ExecuteAsync(new RemoveUnusedUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles,
            Preview = preview
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, result.UsingDirectivesRemoved);
    }

    [SkippableFact]
    public async Task Previews_WithNothingToDo_StillReportZero()
    {
        const string clean = """
            using System;
            namespace TestApp
            {
                public class C { public void M() { Console.WriteLine(1); } }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(clean, "latest");

        var add = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });
        var remove = await new RemoveUnusedUsingsOperation(workspace.Context).ExecuteAsync(new RemoveUnusedUsingsParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(add.Success, add.Error?.Message);
        Assert.Equal(0, add.UsingDirectivesAdded);
        Assert.True(remove.Success, remove.Error?.Message);
        Assert.Equal(0, remove.UsingDirectivesRemoved);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpLangVersion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Case.csproj");
            var sourcePath = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <LangVersion>{langVersion}</LangVersion>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var context = await new MSBuildWorkspaceProvider().CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace { DirectoryPath = directory, SourcePath = sourcePath, Context = context };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* ignore cleanup failures */ }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async Task<List<string>> GetErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { /* ignore locked temp files */ }
            });
        }
    }
}

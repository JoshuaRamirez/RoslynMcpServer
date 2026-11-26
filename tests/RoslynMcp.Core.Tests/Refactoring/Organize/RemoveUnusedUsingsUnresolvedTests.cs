using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Regression tests for #3330: remove_unused_usings deleted usings whose namespace does not
/// resolve (an unrestored package), although the code still uses them.
/// </summary>
public class RemoveUnusedUsingsUnresolvedTests
{
    private const string Source = """
        using System.Text;
        using Newtonsoft.Json;

        namespace App;

        public static class Program
        {
            public static string Run() => JsonConvert.SerializeObject(1);
        }
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedUsing_IsKept_ResolvableUnusedIsRemoved(bool allFiles)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new RemoveUnusedUsingsOperation(workspace.Context).ExecuteAsync(new RemoveUnusedUsingsParams
        {
            SourceFile = allFiles ? null : workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("using Newtonsoft.Json;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("using System.Text;", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task OnlyUnresolvedUsings_NothingRemoved()
    {
        const string source = """
            using Newtonsoft.Json;

            namespace App;

            public static class Program
            {
                public static string Run() => JsonConvert.SerializeObject(1);
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new RemoveUnusedUsingsOperation(workspace.Context).ExecuteAsync(new RemoveUnusedUsingsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion, params (string Name, string Text)[] extraFiles)
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
            foreach (var (name, text) in extraFiles)
                await File.WriteAllTextAsync(Path.Combine(directory, name), text);

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

        /// <summary>Compile errors of the project with every .cs file re-read from disk.</summary>
        public async Task<List<string>> GetDiskErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var project = document.Project;
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.cs"))
            {
                var existing = project.Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.Ordinal));
                var text = Microsoft.CodeAnalysis.Text.SourceText.From(await File.ReadAllTextAsync(path));
                project = existing != null
                    ? existing.WithText(text).Project
                    : project.AddDocument(Path.GetFileName(path), text, filePath: path).Project;
            }

            var compilation = await project.GetCompilationAsync();
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

using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Regression tests for #3270: introduce_field initializeInConstructor on a struct with no explicit
/// constructor synthesized a parameterless struct constructor, which does not run for default instances.
/// </summary>
public class IntroduceFieldStructConstructorTests
{
    private static string Source(string kind) => $$"""
        using System;
        public {{kind}} P
        {
            public int A;
            public int Get()
            {
                return A + 42;
            }
        }
        """;

    [SkippableTheory]
    [InlineData("struct", "latest")]
    [InlineData("struct", "9.0")]
    [InlineData("record struct", "latest")]
    public async Task StructWithoutConstructor_IsRefused(string kind, string langVersion)
    {
        var source = Source(kind);
        await using var workspace = await TempWorkspace.CreateAsync(source, langVersion);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => Run(workspace));

        Assert.Equal(ErrorCodes.ConstructorNotFound, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ClassWithoutConstructor_StillGetsOne()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source("class"), "latest");

        var result = await Run(workspace);

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public P()", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private static Task<RefactoringResult> Run(TempWorkspace workspace) =>
        new IntroduceFieldOperation(workspace.Context).ExecuteAsync(new IntroduceFieldParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = 7,
            StartColumn = 20,
            EndLine = 7,
            EndColumn = 22,
            FieldName = "_k",
            InitializeInConstructor = true
        });

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

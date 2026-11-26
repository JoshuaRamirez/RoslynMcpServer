using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3089: move_type_to_namespace preview reported ReferencesUpdated /
/// UsingDirectivesAdded / UsingDirectivesRemoved = 0 and listed consumer files as a
/// "(multiple files)" pseudo-path, while apply reported the real counts.
/// </summary>
public class MoveTypeToNamespacePreviewCountsTests
{
    private const string Foo = """
        namespace Old.Ns;

        public class Foo
        {
        }
        """;

    private const string C1 = """
        using Old.Ns;

        namespace Other;

        public class C1
        {
            public Foo Make() => new Foo();
        }
        """;

    private const string C2 = """
        using Old.Ns;

        namespace Third;

        public class C2
        {
            public Foo Get(Foo f) => f;
        }
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_ReportsApplyCountsAndRealConsumerFiles(bool allFiles)
    {
        RefactoringResult preview;
        await using (var workspace = await TempWorkspace.CreateAsync(Foo, "latest", ("C1.cs", C1), ("C2.cs", C2)))
        {
            preview = await Run(workspace, allFiles, preview: true);

            Assert.True(preview.Success, preview.Error?.Message);
            Assert.True(preview.Preview);
            var files = preview.PendingChanges!.Select(c => Path.GetFileName(c.File)).ToList();
            Assert.Contains("Program.cs", files);
            Assert.Contains("C1.cs", files);
            Assert.Contains("C2.cs", files);
            Assert.DoesNotContain(preview.PendingChanges!, c => c.File == "(multiple files)");
            Assert.All(preview.PendingChanges!, c => Assert.True(Path.IsPathRooted(c.File), c.File));
            Assert.Equal(Foo, await File.ReadAllTextAsync(workspace.SourcePath));
        }

        await using (var workspace = await TempWorkspace.CreateAsync(Foo, "latest", ("C1.cs", C1), ("C2.cs", C2)))
        {
            var apply = await Run(workspace, allFiles, preview: false);

            Assert.True(apply.Success, apply.Error?.Message);
            Assert.True(apply.ReferencesUpdated > 0);
            Assert.True(apply.UsingDirectivesAdded > 0);
            Assert.Equal(apply.ReferencesUpdated, preview.ReferencesUpdated);
            Assert.Equal(apply.UsingDirectivesAdded, preview.UsingDirectivesAdded);
            Assert.Equal(apply.UsingDirectivesRemoved, preview.UsingDirectivesRemoved);
            Assert.Equal(
                apply.Changes!.FilesModified.Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal),
                preview.PendingChanges!.Select(c => Path.GetFileName(c.File)).Distinct().OrderBy(f => f, StringComparer.Ordinal));
        }
    }

    private static Task<RefactoringResult> Run(TempWorkspace workspace, bool allFiles, bool preview) =>
        new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(allFiles
            ? new MoveTypeToNamespaceParams { AllFiles = true, SourceFile = workspace.SourcePath, TargetNamespace = "New.Ns", Preview = preview }
            : new MoveTypeToNamespaceParams { SourceFile = workspace.SourcePath, SymbolName = "Foo", TargetNamespace = "New.Ns", Preview = preview });

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

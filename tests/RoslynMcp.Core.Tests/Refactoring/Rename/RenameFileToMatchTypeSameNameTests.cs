using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Regression tests for #3112: rename_file_to_match_type reported SymbolAmbiguous for same-named
/// declarations in one file (Result + Result&lt;T&gt;, two partial parts), which all map to one file name.
/// </summary>
public class RenameFileToMatchTypeSameNameTests
{
    private const string Generic = """
        namespace TestApp;

        public class Result { public bool Ok { get; init; } }
        public class Result<T> : Result { public T? Value { get; init; } }
        """;

    private const string Partial = """
        namespace TestApp;
        public partial class Widget { public int A; }
        public partial class Widget { public int B; }
        """;

    [SkippableTheory]
    [InlineData(true, null)]
    [InlineData(true, "Result")]
    [InlineData(false, "Widget")]
    [InlineData(false, null)]
    public async Task SameNamedDeclarations_RenameToThatName(bool generic, string? typeName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(generic ? Generic : Partial, "latest");

        var result = await new RenameFileToMatchTypeOperation(workspace.Context).ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = typeName
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, generic ? "Result.cs" : "Widget.cs")));
        Assert.False(File.Exists(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task AllFiles_RenamesFileWithGenericSibling()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Generic, "latest");

        var result = await new RenameFileToMatchTypeOperation(workspace.Context).ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Result.cs")));
        Assert.False(File.Exists(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task DifferentNames_StillAmbiguous()
    {
        const string source = """
            namespace TestApp;
            public class Alpha { }
            public class Beta { }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RoslynMcp.Core.Refactoring.RefactoringException>(() =>
            new RenameFileToMatchTypeOperation(workspace.Context).ExecuteAsync(new RenameFileToMatchTypeParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(RoslynMcp.Contracts.Errors.ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
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

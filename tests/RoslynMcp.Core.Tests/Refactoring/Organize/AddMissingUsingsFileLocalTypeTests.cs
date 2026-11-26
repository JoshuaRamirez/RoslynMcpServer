using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Regression tests for #3094: add_missing_usings proposed the namespace of a file-local type
/// declared in another file, which can never resolve the name.
/// </summary>
public class AddMissingUsingsFileLocalTypeTests
{
    private const string Program = """
        namespace App;

        public class Consumer
        {
            public int Run() => new Widget().Size;
        }
        """;

    private const string Helpers = """
        namespace App.Internal;

        file class Widget
        {
            public int Size => 1;
        }

        file static class Program2 { static int M() => new Widget().Size; }
        """;

    private const string Models = """
        namespace App.Models.Shapes;

        public class Widget
        {
            public int Size => 2;
        }
        """;

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlyFileLocalCandidateInAnotherFile_AddsNothing(bool allFiles, bool preview)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Program, "latest", ("Helpers.cs", Helpers));

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles,
            Preview = preview
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, result.UsingDirectivesAdded);
        Assert.DoesNotContain(result.PendingChanges ?? [], c => (c.AfterSnippet ?? "").Contains("App.Internal", StringComparison.Ordinal));
        Assert.Equal(Program, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicTypeElsewhere_WinsOverFileLocalType(bool allFiles)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Program, "latest", ("Helpers.cs", Helpers), ("Models.cs", Models));

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("using App.Models.Shapes;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Internal", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private const string SharedFileLocal = """
        namespace App.Internal
        {
            file class Widget
            {
                public int Size => 1;
            }
        }

        namespace App
        {
            public class Consumer
            {
                public int Run() => new Widget().Size;
            }
        }
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileLocalTypeInSameDocument_GetsItsNamespaceUsing(bool allFiles)
    {
        // The document declares the file class itself, so a using for its namespace resolves it
        // (#3094 review): only file-local types from other files are unreachable.
        await using var workspace = await TempWorkspace.CreateAsync(SharedFileLocal, "latest");
        Assert.NotEmpty(await workspace.GetDiskErrorsAsync());

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Equal("using App.Internal;\n" + SharedFileLocal, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private const string HandleProgram = """
        namespace App;

        public class Consumer
        {
            public string Run() => typeof(SafeFileHandle).Name;
        }
        """;

    private const string HandleHelpers = """
        namespace App.Internal;

        file class SafeFileHandle
        {
        }

        file static class Uses { static string M() => typeof(SafeFileHandle).Name; }
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedTypeElsewhere_WinsOverShorterFileLocalNamespace(bool allFiles)
    {
        // App.Internal is shorter than Microsoft.Win32.SafeHandles, so the length tie-break picked the
        // other file's file class and left CS0246 behind before #3094.
        await using var workspace = await TempWorkspace.CreateAsync(HandleProgram, "latest", ("Helpers.cs", HandleHelpers));

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Equal("""
            using Microsoft.Win32.SafeHandles;
            namespace App;

            public class Consumer
            {
                public string Run() => typeof(SafeFileHandle).Name;
            }
            """, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
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

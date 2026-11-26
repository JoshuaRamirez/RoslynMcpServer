using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Regression tests for #3093: add_missing_usings added a namespace using for a name unresolved
/// inside a using alias / using static target, which binds without the sibling usings, so the
/// error stayed and the tool reported UsingDirectivesAdded = 1.
/// </summary>
public class AddMissingUsingsAliasStaticTargetTests
{
    private const string WidgetSource = """
        namespace App.Models;

        public sealed class Widget
        {
            public string Name => "w";
        }

        public static class Helpers
        {
            public static int Twice(int x) => x * 2;
        }
        """;

    [SkippableTheory]
    [InlineData("using W = Widget;", "public static string Run() => new W().Name;", false)]
    [InlineData("using static Helpers;", "public static int Run() => Twice(21);", false)]
    [InlineData("global using W = Widget;", "public static string Run() => new W().Name;", false)]
    [InlineData("global using static Helpers;", "public static int Run() => Twice(21);", false)]
    [InlineData("using W = Widget;", "public static string Run() => new W().Name;", true)]
    [InlineData("using static Helpers;", "public static int Run() => Twice(21);", true)]
    public async Task NameInsideUsingDirectiveTarget_AddsNoUsing(string directive, string member, bool allFiles)
    {
        var program = $$"""
            {{directive}}

            namespace App;

            public static class Program
            {
                {{member}}
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(program, "latest", ("Widget.cs", WidgetSource));

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(0, result.UsingDirectivesAdded);
        Assert.Equal(program, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task InCodeUseOfSameType_StillGetsTheNamespaceUsing()
    {
        const string program = """
            using W = Widget;

            namespace App;

            public static class Program
            {
                public static string Run() => new W().Name + new Widget().Name;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(program, "latest", ("Widget.cs", WidgetSource));

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Contains("using App.Models;", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("""
        namespace App
        {
            using W = Widget;

            public static class Program
            {
                public static string Run() => new W().Name;
            }
        }
        """, false)]
    [InlineData("""
        namespace App
        {
            using static Helpers;

            public static class Program
            {
                public static int Run() => Twice(21);
            }
        }
        """, true)]
    [InlineData("""
        namespace App;

        using W = Widget;

        public static class Program
        {
            public static string Run() => new W().Name;
        }
        """, false)]
    [InlineData("""
        namespace App;

        using static Helpers;

        public static class Program
        {
            public static int Run() => Twice(21);
        }
        """, true)]
    public async Task NameInsideUsingDirectiveWithinNamespace_IsStillFixed(string program, bool allFiles)
    {
        // A using directive inside a namespace (block or after a file-scoped namespace) binds in the
        // namespace's scope, which sees the compilation unit's usings, so adding one fixes it (#3093 review).
        await using var workspace = await TempWorkspace.CreateAsync(program, "latest", ("Widget.cs", WidgetSource));
        Assert.NotEmpty(await workspace.GetDiskErrorsAsync());

        var result = await new AddMissingUsingsOperation(workspace.Context).ExecuteAsync(new AddMissingUsingsParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = allFiles
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, result.UsingDirectivesAdded);
        Assert.Contains("using App.Models;", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
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

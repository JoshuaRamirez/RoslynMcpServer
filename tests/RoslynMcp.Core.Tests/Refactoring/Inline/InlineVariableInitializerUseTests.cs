using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Regression tests for #3551: inline_variable skipped every use of the local that was the
/// entire initializer of another declarator (<c>int m = n;</c>, <c>fixed (int* p = arr)</c>),
/// so the declaration was removed but the use was left behind (CS0103) with SUCCESS.
/// </summary>
public class InlineVariableInitializerUseTests
{
    private const string LocalSource = """
        namespace Demo;

        public class Calc
        {
            private int _i = 4;

            public int Run()
            {
                int n = _i;
                int m = n;
                return m + 1;
            }
        }
        """;

    private const string FixedSource = """
        namespace Demo;

        public class Pins
        {
            private int[] _a = new[] { 7, 8 };

            public unsafe int First()
            {
                int[] arr = _a;
                fixed (int* p = arr)
                {
                    return *p;
                }
            }
        }
        """;

    private const string SameStatementSource = """
        namespace Demo;

        public class Calc
        {
            private int _i = 4;

            public int Run()
            {
                int n = _i, m = n;
                return m + 1;
            }
        }
        """;

    [SkippableFact]
    public async Task SingleSite_UseAsAnotherLocalsInitializer_IsRewritten()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LocalSource, "latest");

        var result = await Inline(workspace, "n");

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("int m = _i;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("= n;", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task SingleSite_UseAsFixedStatementInitializer_IsRewritten()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FixedSource, "latest");

        var result = await Inline(workspace, "arr");

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("fixed (int* p = _a)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("arr", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task SingleSite_UseAsLaterDeclaratorInSameStatement_IsRewritten()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameStatementSource, "latest");

        var result = await Inline(workspace, "n");

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("int m = _i;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("n =", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task AllFiles_UseAsAnotherLocalsInitializer_LeavesNoDanglingUse()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LocalSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("int n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("= n;", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    private static Task<RefactoringResult> Inline(TempWorkspace workspace, string variableName) =>
        new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = variableName
        });

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
                    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
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

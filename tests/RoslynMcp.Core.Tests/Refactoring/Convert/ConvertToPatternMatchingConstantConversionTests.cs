using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3107: convert_to_pattern_matching turned an if-chain arm <c>x == K</c>
/// into the constant pattern <c>K =&gt;</c> even when K has no implicit constant conversion to
/// x's type (<c>char == 65</c>, <c>int == 1L</c>, <c>byte == 300</c>), producing CS0266 / CS0031.
/// </summary>
public class ConvertToPatternMatchingConstantConversionTests
{
    private static string Source(string type, string first, string second) => $$"""
        public static class Codes
        {
            public static string Pick({{type}} x)
            {
                if (x == {{first}}) return "first";
                else if (x == {{second}}) return "second";
                else return "other";
            }
        }
        """;

    [SkippableTheory]
    [InlineData("char", "65", "66")]
    [InlineData("int", "1L", "2L")]
    [InlineData("byte", "300", "7")]
    [InlineData("float", "1.5", "2.5")]
    public async Task SingleSite_ConstantWithoutImplicitConversion_IsCannotConvert(string type, string first, string second)
    {
        var source = Source(type, first, second);
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ConvertToPatternMatchingOperation(workspace.Context).ExecuteAsync(new ConvertToPatternMatchingParams
            {
                SourceFile = workspace.SourcePath,
                Line = 5
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllFiles_ConstantWithoutImplicitConversion_IsLeftAlone(bool preview)
    {
        var source = Source("char", "65", "66");
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new ConvertToPatternMatchingOperation(workspace.Context).ExecuteAsync(new ConvertToPatternMatchingParams
        {
            AllFiles = true,
            Preview = preview
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.PendingChanges ?? []);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [InlineData("long", "1", "2")]
    [InlineData("double", "1", "2")]
    [InlineData("byte", "7", "8")]
    [InlineData("char", "'A'", "'B'")]
    [InlineData("int?", "1", "2")]
    [InlineData("decimal", "1", "2")]
    public async Task ImplicitlyConvertibleConstants_StillConvert(string type, string first, string second)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source(type, first, second), "latest");

        // Preview: the applied file still hits the unrelated whitespace collapse tracked separately,
        // so assert on the normalized after-snippet.
        var result = await new ConvertToPatternMatchingOperation(workspace.Context).ExecuteAsync(new ConvertToPatternMatchingParams
        {
            SourceFile = workspace.SourcePath,
            Line = 5,
            Preview = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var after = Assert.Single(result.PendingChanges!).AfterSnippet;
        Assert.NotNull(after);
        Assert.Contains("x switch", after, StringComparison.Ordinal);
        Assert.Contains($"{first} =>", after, StringComparison.Ordinal);
        Assert.Contains($"{second} =>", after, StringComparison.Ordinal);
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

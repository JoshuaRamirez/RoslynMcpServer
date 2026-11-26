using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3361: convert_foreach_linq moved a null-filtered projection into its own
/// <c>Select</c> lambda, losing the <c>if</c> narrowing, so <c>Where(s =&gt; s != null).Select(s =&gt; s)</c>
/// produced <c>List&lt;string?&gt;</c> for a <c>List&lt;string&gt;</c> target (CS8619, an error under
/// TreatWarningsAsErrors).
/// </summary>
public class ConvertForeachLinqNullableFilterTests
{
    private static string Source(string sourceType, string condition, string projection) => $$"""
        using System.Collections.Generic;
        using System.Linq;

        public static class P
        {
            public static List<string> NonNull({{sourceType}} items)
            {
                var result = new List<string>();
                foreach (var s in items)
                {
                    if ({{condition}})
                        result.Add({{projection}});
                }
                return result;
            }
        }
        """;

    private static string Converted(string sourceType, string statement) => $$"""
        using System.Collections.Generic;
        using System.Linq;

        public static class P
        {
            public static List<string> NonNull({{sourceType}} items)
            {
                var result = new List<string>();
                {{statement}}
                return result;
            }
        }
        """;

    public static TheoryData<string, string, string, string> NullFilters => new()
    {
        { "string?[]", "s != null", "s", "result=items.Where(s=>s != null).Select(s=>s!).ToList();" },
        { "List<string?>", "s is not null", "s", "result=items.Where(s=>s is not null).Select(s=>s!).ToList();" },
        { "IEnumerable<string?>", "s is { }", "s", "result=items.Where(s=>s is { }).Select(s=>s!).ToList();" },
        { "string?[]", "s != null", "s.Trim()", "result=items.Where(s=>s != null).Select(s=>s!.Trim()).ToList();" },
        { "string?[]", "s != null && s.Length > 0", "s + \"!\"", "result=items.Where(s=>s != null && s.Length > 0).Select(s=>s! + \"!\").ToList();" },
        // nameof(s!) is CS8081: identifiers inside nameof are not value uses and keep no '!' (#3361 review).
        { "string?[]", "s != null", "s + nameof(s)", "result=items.Where(s=>s != null).Select(s=>s! + nameof(s)).ToList();" },
        { "string?[]", "s != null", "nameof(s) + s.Length", "result=items.Where(s=>s != null).Select(s=>nameof(s) + s!.Length).ToList();" },
    };

    [SkippableTheory]
    [MemberData(nameof(NullFilters))]
    public async Task SingleSite_NullFilter_KeepsBuildClean(string sourceType, string condition, string projection, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source(sourceType, condition, projection), "latest");
        Assert.Empty(await workspace.GetDiskErrorsAsync());

        var result = await new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Converted(sourceType, expected), await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableTheory]
    [MemberData(nameof(NullFilters))]
    public async Task AllFiles_NullFilter_KeepsBuildClean(string sourceType, string condition, string projection, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source(sourceType, condition, projection), "latest");

        var result = await new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(Converted(sourceType, expected), await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task NonNullableSource_IsNotSuppressed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source("string[]", "s.Length > 0", "s"), "latest");

        var result = await new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("s!", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
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
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
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

        /// <summary>Compile errors (warnings are errors) of the project with every .cs file re-read from disk.</summary>
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

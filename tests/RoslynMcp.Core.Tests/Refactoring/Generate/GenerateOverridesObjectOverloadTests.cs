using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Regression tests for #3113: generate_overrides skipped object.ToString() / Equals(object) /
/// GetHashCode() whenever the type overrode an unrelated same-name overload.
/// </summary>
public class GenerateOverridesObjectOverloadTests
{
    private const string Source = """
        namespace TestApp;

        public class Document
        {
            public virtual string ToString(string format) => format;
            public virtual bool Equals(Document other) => ReferenceEquals(this, other);
            public virtual int GetHashCode(bool deep) => 0;
        }

        public class Report : Document
        {
            public override string ToString(string format) => "report:" + format;
            public override bool Equals(Document other) => base.Equals(other);
            public override int GetHashCode(bool deep) => 1;
        }
        """;

    [SkippableFact]
    public async Task AllMembers_OffersObjectMembersHiddenBehindSameNameOverloads()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new GenerateOverridesOperation(workspace.Context).ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Report"
        });

        Assert.True(result.Success, result.Error?.Message);
        await AssertObjectOverridesAsync(workspace);
    }

    [SkippableFact]
    public async Task NamedMembers_FindsObjectMembersHiddenBehindSameNameOverloads()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new GenerateOverridesOperation(workspace.Context).ExecuteAsync(new GenerateOverridesParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Report",
            Members = ["ToString", "Equals", "GetHashCode"]
        });

        Assert.True(result.Success, result.Error?.Message);
        await AssertObjectOverridesAsync(workspace);
    }

    private static async Task AssertObjectOverridesAsync(TempWorkspace workspace)
    {
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        // object.ToString() is declared string?, so the override may carry the annotation.
        Assert.Matches(@"override string\??\s+ToString\(\)", text);
        Assert.Contains("override bool Equals(object", text, StringComparison.Ordinal);
        Assert.Contains("override int GetHashCode()", text, StringComparison.Ordinal);
        Assert.Contains("override string ToString(string format) => \"report:\"", text, StringComparison.Ordinal);
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

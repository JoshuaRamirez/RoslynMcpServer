using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Encapsulate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Encapsulate;

/// <summary>
/// Regression tests for #3066: encapsulate_field preview snippets showed enum-name modifiers
/// (protectedorinternal), dropped static / readonly / initializers, and never showed the renamed backing field.
/// </summary>
public class EncapsulateFieldPreviewSnippetTests
{
    private const string Source = """
        namespace Sb;
        public class E
        {
            protected internal int _count;
            public static readonly int total = 5;
            public int size;
        }
        public class EUse { public int Get(E e) => e.size + e._count; }
        """;

    [SkippableTheory]
    [InlineData("_count", "protected internal int _count;")]
    [InlineData("total", "public static readonly int total = 5;")]
    [InlineData("size", "public int size;")]
    public async Task BeforeSnippet_IsTheSourceDeclaration(string fieldName, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var change = await PreviewAsync(workspace, fieldName);

        Assert.Equal(expected, change.BeforeSnippet);
        Assert.DoesNotContain("protectedorinternal", change.AfterSnippet, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AfterSnippet_KeepsStaticReadonlyAndInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var change = await PreviewAsync(workspace, "total");

        Assert.Contains("static readonly int", change.AfterSnippet, StringComparison.Ordinal);
        Assert.Contains("= 5", change.AfterSnippet, StringComparison.Ordinal);
        Assert.Contains("Total", change.AfterSnippet, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AfterSnippet_MatchesWhatApplyWrites_IncludingRenamedField()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var change = await PreviewAsync(workspace, "size");
        Assert.Contains("_size", change.AfterSnippet, StringComparison.Ordinal);

        var result = await new EncapsulateFieldOperation(workspace.Context).ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "size"
        });
        Assert.True(result.Success, result.Error?.Message);

        var applied = Squash(await File.ReadAllTextAsync(workspace.SourcePath));
        foreach (var part in change.AfterSnippet!.Split("\n\n"))
            Assert.Contains(Squash(part), applied, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("""
        public class E { public int Size { get; set; } }
        public class E<T> { public int size; }
        """)]
    [InlineData("""
        public class E<T> { public int Size { get; set; } }
        public class E { public int size; }
        """)]
    [InlineData("""
        namespace B { public class E { public int Size { get; set; } } }
        namespace A { public class E { public int size; } }
        """)]
    [InlineData("""
        public class B { public class E { public int Size { get; set; } } }
        public class A { public class E { public int size; } }
        """)]
    public async Task AfterSnippet_ComesFromTheFieldsOwnType_WhenAnotherTypeSharesItsShortName(string source)
    {
        // Another type with the same short name (E vs E<T>, A.E vs B.E) already has a Size property and is
        // declared first; the preview used to show that property instead of the rewritten field + new property.
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var change = await PreviewAsync(workspace, "size");

        Assert.Equal("public int size;", change.BeforeSnippet);
        var parts = change.AfterSnippet!.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Contains("_size", parts[0], StringComparison.Ordinal);
        Assert.Contains("Size", parts[1], StringComparison.Ordinal);
        Assert.Contains("_size", parts[1], StringComparison.Ordinal);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await new EncapsulateFieldOperation(workspace.Context).ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = "size"
        });
        Assert.True(result.Success, result.Error?.Message);

        var applied = Squash(await File.ReadAllTextAsync(workspace.SourcePath));
        foreach (var part in parts)
            Assert.Contains(Squash(part), applied, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PrivateProtectedField_SnippetsShowRealModifiers()
    {
        const string source = """
            namespace Sb;
            public class E
            {
                private protected int _size;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var change = await PreviewAsync(workspace, "_size");

        Assert.Equal("private protected int _size;", change.BeforeSnippet);
        Assert.DoesNotContain("protectedandinternal", change.AfterSnippet, StringComparison.Ordinal);
        Assert.Contains("_size", change.AfterSnippet, StringComparison.Ordinal);
        Assert.Contains("Size", change.AfterSnippet, StringComparison.Ordinal);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private static async Task<PendingChange> PreviewAsync(TempWorkspace workspace, string fieldName)
    {
        var result = await new EncapsulateFieldOperation(workspace.Context).ExecuteAsync(new EncapsulateFieldParams
        {
            SourceFile = workspace.SourcePath,
            FieldName = fieldName,
            Preview = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var change = Assert.Single(result.PendingChanges!);
        Assert.NotNull(change.BeforeSnippet);
        Assert.False(string.IsNullOrWhiteSpace(change.AfterSnippet));
        return change;
    }

    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

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

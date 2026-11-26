using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Inline;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Inline;

/// <summary>
/// Regression tests for #3090: inline_variable treated every identifier under an assignment's
/// left side as a write, so a local only read inside an index (<c>values[index] = 10</c>) was
/// refused with MultipleAssignments (single-site) or silently skipped (allFiles).
/// </summary>
public class InlineVariableAssignmentTargetReadTests
{
    private const string IndexSource = """
        namespace Demo;

        public class Slots
        {
            public int[] Fill(int[] values)
            {
                int index = 2;
                values[index] = 10;
                values[index + 1] += 3;
                return values;
            }
        }
        """;

    [SkippableFact]
    public async Task SingleSite_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "index"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.Contains("values[2 + 1] += 3;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task AllFiles_LocalReadInsideIndexOfTarget_IsInlined()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexSource, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            AllFiles = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("values[2] = 10;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("int index", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SingleSite_ReferenceTypeReceiverOfMemberWrite_IsInlined()
    {
        const string source = """
            namespace Demo;

            public class Widget { public string Name = ""; }

            public class Holder
            {
                private Widget _current = new Widget();

                public Widget Rename()
                {
                    Widget target = _current;
                    target.Name = "renamed";
                    return target;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
        {
            SourceFile = workspace.SourcePath,
            VariableName = "target"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("_current.Name = \"renamed\";", text, StringComparison.Ordinal);
        Assert.Contains("return _current;", text, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("copy.Value = 5;")]
    [InlineData("(copy).Value = 5;")]
    [InlineData("copy.Inner.Value = 5;")]
    [InlineData("copy = default;")]
    [InlineData("(copy, other) = (other, copy);")]
    public async Task SingleSite_LocalItselfOrValueTypeReceiverWritten_StillRefused(string write)
    {
        var source = $$"""
            namespace Demo;

            public struct Cell { public int Value; public Inner Inner; }
            public struct Inner { public int Value; }

            public class Grid
            {
                private Cell _cell;

                public int Touch()
                {
                    Cell copy = _cell;
                    Cell other = _cell;
                    {{write}}
                    return copy.Value + other.Value;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new InlineVariableOperation(workspace.Context).ExecuteAsync(new InlineVariableParams
            {
                SourceFile = workspace.SourcePath,
                VariableName = "copy"
            }));

        Assert.Equal(ErrorCodes.MultipleAssignments, ex.ErrorCode);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
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

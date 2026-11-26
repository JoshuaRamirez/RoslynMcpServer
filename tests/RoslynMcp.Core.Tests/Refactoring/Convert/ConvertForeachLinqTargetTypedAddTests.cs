using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3038: convert_foreach_linq pasted a target-typed Add argument with no natural type
/// (null, default, new(), a collection expression) into Select, which cannot infer its element type.
/// </summary>
public class ConvertForeachLinqTargetTypedAddTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Linq;
        public class Point { public int X; }
        public static class Program
        {
            public static int Run()
            {
                var xs = new[] { 1, -2, 3 };
                var names = new List<string?>();
                foreach (var x in xs)
                    names.Add(null);
                var points = new List<Point>();
                foreach (var x in xs)
                    points.Add(new() { X = x });
                var pairs = new List<int[]>();
                foreach (var x in xs)
                    pairs.Add([x, x * 2]);
                var maybe = new List<int?>();
                foreach (var x in xs)
                    if (x > 0)
                        maybe.Add(default);
                var flags = new List<string?>();
                foreach (var x in xs)
                    flags.Add(x > 0 ? null : default);
                return names.Count + points.Count + pairs.Count + maybe.Count + flags.Count;
            }
        }
        """;

    [SkippableTheory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(23)]
    public async Task SingleSite_TargetTypedProjection_IsRefused(int line)
    {
        await using var workspace = await TempWorkspace.CreateAsync("#nullable enable\n" + Source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
            {
                SourceFile = workspace.SourcePath,
                Line = line + 1
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllFiles_LeavesTargetTypedProjectionsAlone(bool preferQuerySyntax)
    {
        var source = "#nullable enable\n" + Source;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
        {
            AllFiles = true,
            PreferQuerySyntax = preferQuerySyntax
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task TypedProjection_StillConverts()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public static class Program
            {
                public static int Run()
                {
                    var xs = new[] { 1, -2, 3 };
                    var names = new List<string>();
                    foreach (var x in xs)
                        names.Add(x.ToString());
                    return names.Count;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "latest");

        var result = await new ConvertForeachLinqOperation(workspace.Context).ExecuteAsync(new ConvertForeachLinqParams
        {
            SourceFile = workspace.SourcePath,
            Line = 9
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("Select(", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
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

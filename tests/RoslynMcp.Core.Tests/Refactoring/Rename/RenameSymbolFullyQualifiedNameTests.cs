using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Regression tests for #3289: the reported <c>symbol.fullyQualifiedName</c> was built with a string replace of the
/// old name, so namespaces, containing types and parameter types that merely contain it were "renamed" too.
/// </summary>
public class RenameSymbolFullyQualifiedNameTests
{
    private const string Source = """
        namespace Widgets.WidgetCore
        {
            public class WidgetFactory
            {
                public static Widget Widget() => new Widget();
            }

            public class Widget { public int Get(int Widget) => Widget; }

            public class Runner { }

            public class Calc
            {
                public void Run(Runner runner) { }
            }
        }
        """;

    [SkippableTheory]
    [InlineData("Widget", 8, "Gadget", "Widgets.WidgetCore.Gadget")]
    [InlineData("Widget", 5, "Make", "Widgets.WidgetCore.WidgetFactory.Make()")]
    [InlineData("Run", 14, "Go", "Widgets.WidgetCore.Calc.Go(Widgets.WidgetCore.Runner)")]
    public async Task RenameSymbol_ReportsFullyQualifiedNameWithOnlyTheSymbolRenamed(
        string symbolName, int line, string newName, string expectedFqn)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new RenameSymbolOperation(workspace.Context).ExecuteAsync(new RenameSymbolParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = symbolName,
            Line = line,
            NewName = newName
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Symbol);
        Assert.Equal(newName, result.Symbol.Name);
        Assert.Equal(expectedFqn, result.Symbol.FullyQualifiedName);
        var errors = await workspace.GetErrorsAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
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

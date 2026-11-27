using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3272: the SymbolAmbiguous <c>details.matches</c> of move_type_to_file /
/// move_type_to_namespace reported line 1 for every candidate instead of each declaration's line.
/// </summary>
public class MoveTypeAmbiguousMatchesTests
{
    private const string Source = """
        namespace Billing
        {
            public class Widget
            {
                public string Kind => "billing";
            }
        }

        namespace Shipping
        {
            public class Widget
            {
                public string Kind => "shipping";
            }
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToFile_AmbiguousName_ReportsEachDeclarationLine_AndThatLineResolves()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");
        var targetFile = Path.Combine(workspace.DirectoryPath, "Widget.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                TargetFile = targetFile,
                CreateTargetFile = true
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.Equal(new[] { 3, 11 }, Assert.IsAssignableFrom<IEnumerable<int>>(ex.Details["matches"]));

        // Retrying with a reported line picks that declaration.
        var result = await new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = 11,
            TargetFile = targetFile,
            CreateTargetFile = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("\"shipping\"", await File.ReadAllTextAsync(targetFile), StringComparison.Ordinal);
        Assert.Contains("\"billing\"", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AmbiguousName_ReportsEachDeclarationLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                TargetNamespace = "Receiving"
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.Equal(new[] { 3, 11 }, Assert.IsAssignableFrom<IEnumerable<int>>(ex.Details["matches"]));
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

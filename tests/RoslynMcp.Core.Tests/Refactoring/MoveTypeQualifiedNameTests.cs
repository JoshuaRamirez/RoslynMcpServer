using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3273: move_type_to_file / move_type_to_namespace dropped the namespace
/// qualifier of a qualified symbolName, so a wrong qualifier moved another type and the right one
/// could not tell same-named types apart.
/// </summary>
public class MoveTypeQualifiedNameTests
{
    private const string TwoWidgets = """
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

    private const string OneWidget = """
        namespace Billing
        {
            public class Widget
            {
                public string Kind => "billing";
            }
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToFile_QualifiedName_PicksThatNamespacesType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoWidgets, "latest");
        var targetFile = Path.Combine(workspace.DirectoryPath, "Widget.cs");

        var result = await new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shipping.Widget",
            TargetFile = targetFile,
            CreateTargetFile = true
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("\"shipping\"", await File.ReadAllTextAsync(targetFile), StringComparison.Ordinal);
        Assert.Contains("\"billing\"", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_WrongQualifier_IsSymbolNotFound_AndMovesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(OneWidget, "latest");
        var targetFile = Path.Combine(workspace.DirectoryPath, "Widget.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Shipping.Widget",
                TargetFile = targetFile,
                CreateTargetFile = true
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.False(File.Exists(targetFile));
        Assert.Equal(OneWidget, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_QualifiedName_PicksThatNamespacesType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(TwoWidgets, "latest");

        var result = await new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shipping.Widget",
            TargetNamespace = "Receiving"
        });

        Assert.True(result.Success, result.Error?.Message);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var receiving = text.IndexOf("Receiving", StringComparison.Ordinal);
        Assert.True(receiving >= 0, text);
        Assert.True(text.IndexOf("\"shipping\"", StringComparison.Ordinal) > receiving, text);
        Assert.True(text.IndexOf("\"billing\"", StringComparison.Ordinal) < receiving, text);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_WrongQualifier_IsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(OneWidget, "latest");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Nope.Widget",
                TargetNamespace = "Receiving"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(OneWidget, await File.ReadAllTextAsync(workspace.SourcePath));
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

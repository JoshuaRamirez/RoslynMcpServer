using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3274: move_type_to_file / move_type_to_namespace matched <c>line</c> only
/// against the declaration's start line, which is the attribute line for an attributed type, so
/// the identifier line that search_symbols reports failed with SymbolNotFound.
/// </summary>
public class MoveTypeAttributedLineTests
{
    // Billing.Widget: attribute on line 3, identifier on line 4. Shipping.Widget: line 8.
    private const string Source = """
        namespace Billing
        {
            [System.Serializable]
            public class Widget { public string Kind => "billing"; }
        }
        namespace Shipping
        {
            public class Widget { public string Kind => "shipping"; }
        }
        """;

    [SkippableTheory]
    [InlineData(4)]
    [InlineData(3)]
    public async Task MoveTypeToFile_AttributedType_ResolvesByIdentifierOrAttributeLine(int line)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");
        var targetFile = Path.Combine(workspace.DirectoryPath, "W.cs");

        var result = await new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            TargetFile = targetFile,
            CreateTargetFile = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var moved = await File.ReadAllTextAsync(targetFile);
        Assert.Contains("\"billing\"", moved, StringComparison.Ordinal);
        Assert.Contains("Serializable", moved, StringComparison.Ordinal);
        Assert.Contains("\"shipping\"", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
        Assert.NotNull(result.Symbol?.PreviousLocation);
        Assert.Equal(4, result.Symbol!.PreviousLocation!.Line);
        Assert.Equal(18, result.Symbol.PreviousLocation.Column);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AttributedType_ResolvesByIdentifierLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");

        var result = await new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = 4,
            TargetNamespace = "Other"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("Billing", result.Symbol?.PreviousNamespace);
        Assert.Equal(4, result.Symbol?.PreviousLocation?.Line);
        var text = await File.ReadAllTextAsync(workspace.SourcePath);
        var other = text.IndexOf("namespace Other", StringComparison.Ordinal);
        var shipping = text.IndexOf("namespace Shipping", StringComparison.Ordinal);
        Assert.True(other >= 0 && shipping > other, text);
        var billing = text.IndexOf("\"billing\"", StringComparison.Ordinal);
        Assert.True(billing > other && billing < shipping, text);
        Assert.True(text.IndexOf("\"shipping\"", StringComparison.Ordinal) > shipping, text);
    }

    [SkippableFact]
    public async Task MoveTypeToFile_LineInsideBody_IsStillSymbolNotFound()
    {
        // Only the start line and the identifier line select a type; an unrelated line does not.
        await using var workspace = await TempWorkspace.CreateAsync(Source, "latest");
        var targetFile = Path.Combine(workspace.DirectoryPath, "W.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToFileOperation(workspace.Context).ExecuteAsync(new MoveTypeToFileParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = 6,
                TargetFile = targetFile,
                CreateTargetFile = true
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.False(File.Exists(targetFile));
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

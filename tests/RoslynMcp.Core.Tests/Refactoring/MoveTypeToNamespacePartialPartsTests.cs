using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Regression tests for #3550: single-site move_type_to_namespace on one part of a
/// multi-file partial type moved only the sourceFile part, leaving Old.Ns.Part and
/// New.Ns.Part, so a consumer importing both namespaces failed with CS0104 while the
/// tool reported success. Single-site now refuses, as allFiles already skips these.
/// </summary>
public class MoveTypeToNamespacePartialPartsTests
{
    private const string PartA = """
        namespace Old.Ns;

        public partial class Part
        {
            public int A() => 1;
        }
        """;

    private const string PartB = """
        namespace Old.Ns;

        public partial class Part
        {
            public int B() => 2;
        }
        """;

    private const string Consumer = """
        using Old.Ns;

        namespace Other;

        public class Use
        {
            public int Sum(Part p) => p.A() + p.B();
        }
        """;

    private const string Single = """
        namespace Old.Ns;

        public partial class Solo
        {
            public int A() => 1;
        }
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleSite_PartialWithPartInAnotherFile_IsRefusedAndNothingChanges(bool preview)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartA, "latest", ("PartB.cs", PartB), ("Use.cs", Consumer));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Part",
                TargetNamespace = "New.Ns",
                Preview = preview
            }));

        Assert.Equal(ErrorCodes.RoslynError, ex.ErrorCode);
        Assert.Contains("partial", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PartB.cs", ex.Message, StringComparison.Ordinal);

        Assert.Equal(PartA, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(PartB, await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "PartB.cs")));
        Assert.Equal(Consumer, await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "Use.cs")));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task SingleSite_PartialFromTheOtherFile_IsAlsoRefused()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartA, "latest", ("PartB.cs", PartB), ("Use.cs", Consumer));

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = Path.Combine(workspace.DirectoryPath, "PartB.cs"),
                SymbolName = "Part",
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.RoslynError, ex.ErrorCode);
        Assert.Equal(PartB, await File.ReadAllTextAsync(Path.Combine(workspace.DirectoryPath, "PartB.cs")));
        Assert.Empty(await workspace.GetDiskErrorsAsync());
    }

    [SkippableFact]
    public async Task SingleSite_PartialWithOnlyOneDeclaration_StillMoves()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Single, "latest");

        var result = await new MoveTypeToNamespaceOperation(workspace.Context).ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Solo",
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
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

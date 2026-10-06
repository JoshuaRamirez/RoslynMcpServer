using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindImplementationsOperation"/> optional
/// <c>implementationFile</c> (restricts reported implementations to one file; peer of
/// <c>find_references</c> <c>referenceFile</c> / <c>find_callers</c> <c>callerFile</c>).
/// </summary>
public class FindImplementationsOperationTests
{
    // IRunner is declared in IRunner.cs (line 1, column 18). It is implemented twice in ImplA.cs
    // (A1, A2), once in ImplB.cs (B1), and once by partial class Split whose parts live in
    // SplitOne.cs (declares the interface) and SplitTwo.cs (declares Run). Empty.cs has none.
    private static readonly Dictionary<string, string> MultiFileSources = new()
    {
        ["IRunner.cs"] = """
            public interface IRunner { void Run(); }
            """,
        ["ImplA.cs"] = """
            public class A1 : IRunner { public void Run() { } }
            public class A2 : IRunner { public void Run() { } }
            """,
        ["ImplB.cs"] = """
            public class B1 : IRunner { public void Run() { } }
            """,
        ["SplitOne.cs"] = """
            public partial class Split : IRunner { }
            """,
        ["SplitTwo.cs"] = """
            // second part of Split
            public partial class Split { public void Run() { } }
            """,
        ["Empty.cs"] = """
            public class Unrelated
            {
                public void Nothing() { }
            }
            """
    };

    [Fact]
    public void ImplementationFile_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.ImplementationFile);
    }

    [SkippableFact]
    public async Task FindImplementations_OmittedImplementationFile_ReturnsImplementationsFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2", "B1", "Split" }, result.Data.Implementations.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal(2, result.Data.Implementations.Count(i => SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(1, result.Data.Implementations.Count(i => SamePath(i.File, workspace.PathOf("ImplB.cs"))));
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2" }, result.Data.Implementations.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(new[] { 1, 2 }, result.Data.Implementations.Select(i => i.Line).OrderBy(l => l));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("IRunner", result.Data.SymbolName);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFile_LineColumnResolution_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            Line = 1,
            Column = 18,
            ImplementationFile = workspace.PathOf("ImplB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.Equal("B1", only.Name);
        Assert.True(SamePath(only.File, workspace.PathOf("ImplB.cs")));
        Assert.Equal(1, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileIsSecondaryPartialPart_ReportsThatPart()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("SplitTwo.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.Equal("Split", only.Name);
        // Reported at the part declared in implementationFile, not the first source location.
        Assert.True(SamePath(only.File, workspace.PathOf("SplitTwo.cs")));
        Assert.Equal(2, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithNoImplementations_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Implementations);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.True(SamePath(only.File, workspace.PathOf("ImplA.cs")));
        // TotalCount reflects the filtered set (2 in ImplA.cs), not the solution-wide 4.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Implementations);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NonexistentImplementationFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_RelativeImplementationFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = "ImplA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_NonCsImplementationFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_AliasedImplementationFilePath_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "ImplA.cs");

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Implementations.Count);
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_CaseOnlyDifferentImplementationFile_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        var caseVariant = workspace.PathOf("IMPLA.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to ImplA.cs. On case-sensitive volumes (Linux) implementationFile must exist to
        // pass validation (same as find_references referenceFile), so create an
        // implementation-free stub at the case-variant path; the documented OrdinalIgnoreCase
        // comparison must still match ImplA.cs's implementations.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no implementations");

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Implementations.Count);
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync(new Dictionary<string, string> { [fileName] = source }, fileName);

        public static async Task<TempWorkspace> CreateAsync(IReadOnlyDictionary<string, string> files, string? primaryFile = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindImplementations_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            foreach (var (name, source) in files)
                await File.WriteAllTextAsync(Path.Combine(directory, name), source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var name in files.Keys)
                {
                    var filePath = Path.Combine(directory, name);
                    if (context.GetDocumentByPath(filePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {filePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = Path.Combine(directory, primaryFile ?? files.Keys.First()),
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // ignore cleanup failures
                }

                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
                catch
                {
                    // ignore locked temp files
                }
            });
        }
    }
}

using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindCallersOperation"/> optional <c>callerFile</c>
/// (restricts reported call sites to callers located in that file; peer of
/// <c>search_symbols</c> / <c>get_diagnostics</c> optional <c>sourceFile</c>).
/// </summary>
public class FindCallersOperationTests
{
    // Target.Run is declared in Target.cs and called twice from CallerA.cs and once from CallerB.cs.
    // Empty.cs has no callers.
    private const string TargetSource = """
        public class Target
        {
            public void Run() { }
        }
        """;

    private const string CallerASource = """
        public class CallerA
        {
            public void First(Target t) { t.Run(); }
            public void Second(Target t) { t.Run(); }
        }
        """;

    private const string CallerBSource = """
        public class CallerB
        {
            public void Third(Target t) { t.Run(); }
        }
        """;

    private const string EmptySource = """
        public class Unrelated
        {
            public void Nothing() { }
        }
        """;

    [Fact]
    public void CallerFile_DefaultsToNull()
    {
        var @params = new FindCallersParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.CallerFile);
    }

    [SkippableFact]
    public async Task FindCallers_OmittedCallerFile_ReturnsCallersFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(3, result.Data.Callers.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal(2, result.Data.Callers.Count(c => SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(1, result.Data.Callers.Count(c => SamePath(c.File, workspace.PathOf("CallerB.cs"))));
    }

    [SkippableFact]
    public async Task FindCallers_CallerFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Callers.Count);
        Assert.All(result.Data.Callers, c => Assert.True(SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(new[] { "First", "Second" }, result.Data.Callers.Select(c => c.CallerName).OrderBy(n => n));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("Run", result.Data.SymbolName);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileOtherFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            Line = 3,
            Column = 17,
            CallerFile = workspace.PathOf("CallerB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Callers);
        Assert.Equal("Third", only.CallerName);
        Assert.True(SamePath(only.File, workspace.PathOf("CallerB.cs")));
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithNoCallers_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Callers);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Callers);
        Assert.True(SamePath(only.File, workspace.PathOf("CallerA.cs")));
        // TotalCount reflects the filtered set (2 in CallerA.cs), not the solution-wide 3.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Callers);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_NonexistentCallerFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_RelativeCallerFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = "CallerA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_NonCsCallerFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_AliasedCallerFilePath_StillMatchesCallSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "CallerA.cs");

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Callers.Count);
        Assert.All(result.Data.Callers, c => Assert.True(SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static async Task<TempWorkspace> CreateAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindCallers_" + Guid.NewGuid().ToString("N"));
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

            var files = new Dictionary<string, string>
            {
                ["Target.cs"] = TargetSource,
                ["CallerA.cs"] = CallerASource,
                ["CallerB.cs"] = CallerBSource,
                ["Empty.cs"] = EmptySource
            };

            foreach (var (name, source) in files)
                await File.WriteAllTextAsync(Path.Combine(directory, name), source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var name in files.Keys)
                {
                    if (context.GetDocumentByPath(Path.Combine(directory, name)) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {name}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
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

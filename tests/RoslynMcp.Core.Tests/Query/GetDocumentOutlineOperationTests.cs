using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDocumentOutlineOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="GetDiagnosticsOperation"/> / <see cref="SearchSymbolsOperation"/>).
/// </summary>
public class GetDocumentOutlineOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("namespace N { class C { } }\n");
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetDocumentOutline_OmittedMaxResults_ReturnsFullTreeNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public int Prop { get; set; }
                    public void M() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 4, $"Expected >= 4 outline nodes, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, CountNodes(result.Data.Entries));
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public int Prop { get; set; }
                    public void M() { }
                    public void N2() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 5);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxResults = 3
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(3, CountNodes(capped.Data.Entries));
        Assert.True(capped.Data.Truncated);

        // Deterministic DFS pre-order prefix of today's tree:
        // N (ns) -> C (class) -> Prop (first member)
        Assert.Single(capped.Data.Entries);
        Assert.Equal("N", capped.Data.Entries[0].Name);
        Assert.Equal("Namespace", capped.Data.Entries[0].Kind);
        Assert.NotNull(capped.Data.Entries[0].Children);
        Assert.Single(capped.Data.Entries[0].Children!);
        Assert.Equal("C", capped.Data.Entries[0].Children![0].Name);
        Assert.Equal("Class", capped.Data.Entries[0].Children![0].Kind);
        Assert.NotNull(capped.Data.Entries[0].Children![0].Children);
        Assert.Single(capped.Data.Entries[0].Children![0].Children!);
        Assert.Equal(full.Data.Entries[0].Children![0].Children![0].Name,
            capped.Data.Entries[0].Children![0].Children![0].Name);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public void M() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, CountNodes(capped.Data.Entries));
        Assert.False(capped.Data.Truncated);
    }

    #endregion

    private static int CountNodes(IReadOnlyList<OutlineEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (entry.Children is { Count: > 0 })
                count += CountNodes(entry.Children);
        }

        return count;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDocumentOutline_" + Guid.NewGuid().ToString("N"));
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

            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(path) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {path}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = path,
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
                    // ignore cleanup failures
                }
            });
        }
    }
}

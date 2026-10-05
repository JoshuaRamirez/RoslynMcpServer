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

    [Fact]
    public void MaxDepth_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.MaxDepth);
    }

    [SkippableFact]
    public async Task Validate_MaxDepthZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("namespace N { class C { } }\n");
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxDepth = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxDepth must be >= 1.", ex.Message);
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

    #region Execute maxDepth

    private const string DepthSource = """
        namespace N
        {
            public class A
            {
                public int P { get; set; }
                public void M() { }

                public class Inner
                {
                    public void InnerM() { }
                }
            }

            public class B
            {
                public void BM() { }
            }

            public enum E
            {
                One,
                Two
            }
        }
        """;

    [SkippableFact]
    public async Task GetDocumentOutline_OmittedMaxDepth_MatchesTodayFullTree()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        // N, A, Inner, InnerM, M, P, B, BM, E, One, Two => 11 nodes; deepest path N > A > Inner > InnerM
        Assert.Equal(11, full.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, CountNodes(full.Data.Entries));
        Assert.Equal(4, MaxTreeDepth(full.Data.Entries));
        Assert.False(full.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthOne_ReturnsRootsOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(11, capped.Data.TotalCount);
        var root = Assert.Single(capped.Data.Entries);
        Assert.Equal("N", root.Name);
        Assert.Equal("Namespace", root.Kind);
        Assert.Null(root.Children);
        Assert.True(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthTwo_ReturnsAllTopLevelTypesWithoutMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var root = Assert.Single(capped.Data.Entries);
        Assert.NotNull(root.Children);
        Assert.Equal(
            full.Data.Entries[0].Children!.Select(c => (c.Name, c.Kind, c.Line, c.Column, c.Accessibility, c.ReturnType)),
            root.Children!.Select(c => (c.Name, c.Kind, c.Line, c.Column, c.Accessibility, c.ReturnType)));
        Assert.Equal(new[] { "A", "B", "E" }, root.Children!.Select(c => c.Name));
        Assert.All(root.Children!, c => Assert.Null(c.Children));
        Assert.Equal(4, CountNodes(capped.Data.Entries));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthAtOrAboveTreeDepth_ReturnsFullTreeNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);

        foreach (var maxDepth in new[] { 4, 10 })
        {
            var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxDepth = maxDepth
            });

            Assert.True(capped.Success);
            Assert.NotNull(capped.Data);
            Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
            Assert.Equal(full.Data.TotalCount, CountNodes(capped.Data.Entries));
            Assert.Equal(Flatten(full.Data.Entries), Flatten(capped.Data.Entries));
            Assert.False(capped.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthWithMaxResults_AppliesDepthCapFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // Depth cap 2 keeps N, A, B, E (4 nodes); maxResults 3 then keeps the DFS pre-order prefix N, A, B.
        // Without the depth cap, maxResults 3 would be N, A, Inner (budget spent inside A).
        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2,
            MaxResults = 3
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(11, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(new[] { "N", "A", "B" }, Flatten(capped.Data.Entries).Select(e => e.Name));
        var root = Assert.Single(capped.Data.Entries);
        Assert.All(root.Children!, c => Assert.Null(c.Children));

        // maxResults at or above the depth-capped node count: only the depth cap applies.
        var depthOnly = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2,
            MaxResults = 4
        });

        Assert.True(depthOnly.Success);
        Assert.NotNull(depthOnly.Data);
        Assert.Equal(11, depthOnly.Data.TotalCount);
        Assert.True(depthOnly.Data.Truncated);
        Assert.Equal(new[] { "N", "A", "B", "E" }, Flatten(depthOnly.Data.Entries).Select(e => e.Name));
    }

    #endregion

    private static int MaxTreeDepth(IReadOnlyList<OutlineEntry>? entries)
    {
        if (entries is not { Count: > 0 })
            return 0;

        return 1 + entries.Max(e => MaxTreeDepth(e.Children));
    }

    private static List<(string Name, string Kind, int Line, int Column, string? Accessibility, string? ReturnType, int Depth)> Flatten(
        IReadOnlyList<OutlineEntry> entries, int depth = 1)
    {
        var list = new List<(string, string, int, int, string?, string?, int)>();
        foreach (var e in entries)
        {
            list.Add((e.Name, e.Kind, e.Line, e.Column, e.Accessibility, e.ReturnType, depth));
            if (e.Children is { Count: > 0 })
                list.AddRange(Flatten(e.Children, depth + 1));
        }

        return list;
    }

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

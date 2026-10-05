using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GoToDefinitionOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="GetTypeHierarchyOperation"/> / <see cref="FindImplementationsOperation"/>).
/// </summary>
public class GoToDefinitionOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GoToDefinitionParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GoToDefinitionParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    // Partial-class parts are resolved by position (line 1, column 15 = "Widget" in the first part);
    // name-based resolution reports each partial declaration as a separate candidate.

    [SkippableFact]
    public async Task GoToDefinition_OmittedMaxResults_ReturnsAllPartialLocationsNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.Equal(result.Data.TotalCount, result.Data.Definitions.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.Equal(3, full.Data.TotalCount);

        var capped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Definitions.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(full.Data.Definitions[i].File, capped.Data.Definitions[i].File);
            Assert.Equal(full.Data.Definitions[i].Line, capped.Data.Definitions[i].Line);
            Assert.Equal(full.Data.Definitions[i].Column, capped.Data.Definitions[i].Column);
        }
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var atTotal = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 3
        });

        Assert.True(atTotal.Success);
        Assert.NotNull(atTotal.Data);
        Assert.Equal(3, atTotal.Data.TotalCount);
        Assert.Equal(3, atTotal.Data.Definitions.Count);
        Assert.False(atTotal.Data.Truncated);

        var above = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 50
        });

        Assert.True(above.Success);
        Assert.NotNull(above.Data);
        Assert.Equal(3, above.Data.TotalCount);
        Assert.Equal(3, above.Data.Definitions.Count);
        Assert.False(above.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_SingleDefinition_MaxResultsOne_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Solo { }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Solo",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Single(result.Data.Definitions);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGoToDefinition_" + Guid.NewGuid().ToString("N"));
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
                    // ignore locked temp files
                }
            });
        }
    }
}

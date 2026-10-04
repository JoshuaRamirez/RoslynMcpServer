using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetTypeHierarchyOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="FindImplementationsOperation"/> / <see cref="GetDiagnosticsOperation"/>).
/// </summary>
public class GetTypeHierarchyOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            """);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Base",
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetTypeHierarchy_OmittedMaxResults_ReturnsFullDerivedListNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            class C : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 derived, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.DerivedTypes.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            class C : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.DerivedTypes.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.DerivedTypes[0].FullyQualifiedName, capped.Data.DerivedTypes[0].FullyQualifiedName);
        Assert.Equal(full.Data.DerivedTypes[1].FullyQualifiedName, capped.Data.DerivedTypes[1].FullyQualifiedName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.DerivedTypes.Count);
        Assert.False(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsDoesNotCapBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            interface IMarker {}
            class Mid : System.Object, IMarker {}
            class Leaf : Mid {}
            class Other : Mid {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mid",
            Direction = "Both"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);
        Assert.NotEmpty(full.Data.Interfaces);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mid",
            Direction = "Both",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Single(capped.Data.DerivedTypes);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(full.Data.BaseTypes.Count, capped.Data.BaseTypes.Count);
        Assert.Equal(full.Data.Interfaces.Count, capped.Data.Interfaces.Count);
        Assert.Equal(
            full.Data.Interfaces.Select(i => i.FullyQualifiedName).ToArray(),
            capped.Data.Interfaces.Select(i => i.FullyQualifiedName).ToArray());
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetTypeHierarchy_" + Guid.NewGuid().ToString("N"));
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

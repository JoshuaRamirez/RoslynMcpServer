using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDiagnosticsOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="FindReferencesOperation"/> / <see cref="SearchSymbolsOperation"/>).
/// </summary>
public class GetDiagnosticsOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { MaxResults = 0 }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetDiagnostics_OmittedMaxResults_ReturnsFullListNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 diagnostics, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.Diagnostics.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.Diagnostics[0].Id, capped.Data.Diagnostics[0].Id);
        Assert.Equal(full.Data.Diagnostics[0].Message, capped.Data.Diagnostics[0].Message);
        Assert.Equal(full.Data.Diagnostics[1].Id, capped.Data.Diagnostics[1].Id);
        Assert.Equal(full.Data.Diagnostics[1].Message, capped.Data.Diagnostics[1].Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.Diagnostics.Count);
        Assert.False(capped.Data.Truncated);
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnostics_" + Guid.NewGuid().ToString("N"));
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

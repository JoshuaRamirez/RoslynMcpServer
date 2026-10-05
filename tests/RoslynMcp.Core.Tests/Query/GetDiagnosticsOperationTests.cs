using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDiagnosticsOperation"/> optional <c>maxResults</c> and <c>diagnosticIds</c>
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

    #region diagnosticIds filter

    [Fact]
    public void DiagnosticIds_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.DiagnosticIds);
    }

    [SkippableFact]
    public async Task Validate_DiagnosticIdsWhitespaceEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030", " " } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("diagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_EmptyDiagnosticIds_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var empty = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = Array.Empty<string>()
        });

        Assert.True(omitted.Success);
        Assert.True(empty.Success);
        Assert.Equal(omitted.Data!.TotalCount, empty.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            empty.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_KeepsOnlyMatchingIds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS0168" }
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotEmpty(filtered.Data.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS0168", d.Id));
        Assert.Equal(full.Data.Diagnostics.Count(d => d.Id == "CS0168"), filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_MultipleIdsCaseInsensitive()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Id is "CS1030" or "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "cs1030", " Cs0168 " }
        });

        Assert.True(filtered.Success);
        Assert.Equal(expected, filtered.Data!.TotalCount);
        Assert.Equal(expected, filtered.Data.Diagnostics.Count);
        Assert.All(filtered.Data.Diagnostics, d => Assert.True(d.Id is "CS1030" or "CS0168", d.Id));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS9999" }
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIdsWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" }
        });
        Assert.True(filtered.Success);
        Assert.True(filtered.Data!.TotalCount >= 3, $"Expected >= 3 CS1030, got {filtered.Data.TotalCount}");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(filtered.Data.TotalCount, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Diagnostics, d => Assert.Equal("CS1030", d.Id));
    }

    private const string MixedSource = """
        #warning One
        #warning Two
        #warning Three
        class C
        {
            void M()
            {
                int unused;
            }
        }
        """;

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

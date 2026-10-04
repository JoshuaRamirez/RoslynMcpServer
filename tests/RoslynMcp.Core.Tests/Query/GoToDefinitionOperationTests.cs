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
            class Foo {}
            """);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GoToDefinitionParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Foo",
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GoToDefinition_OmittedMaxResults_ReturnsFullDefinitionsNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("PartA.cs", "partial class UniquePartial { public void A() { } }\n"),
            ("PartB.cs", "partial class UniquePartial { public void B() { } }\n"),
            ("PartC.cs", "partial class UniquePartial { public void C() { } }\n"));

        var operation = new GoToDefinitionOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "UniquePartial"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 definitions, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.Definitions.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("PartA.cs", "partial class UniquePartial { public void A() { } }\n"),
            ("PartB.cs", "partial class UniquePartial { public void B() { } }\n"),
            ("PartC.cs", "partial class UniquePartial { public void C() { } }\n"));

        var operation = new GoToDefinitionOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "UniquePartial"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "UniquePartial",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Definitions.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.Definitions[0].File, capped.Data.Definitions[0].File);
        Assert.Equal(full.Data.Definitions[0].Line, capped.Data.Definitions[0].Line);
        Assert.Equal(full.Data.Definitions[1].File, capped.Data.Definitions[1].File);
        Assert.Equal(full.Data.Definitions[1].Line, capped.Data.Definitions[1].Line);
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("PartA.cs", "partial class UniquePartial { public void A() { } }\n"),
            ("PartB.cs", "partial class UniquePartial { public void B() { } }\n"));

        var operation = new GoToDefinitionOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "UniquePartial"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "UniquePartial",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.Definitions.Count);
        Assert.False(capped.Data.Truncated);
    }

    #endregion

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileCoreAsync(files);

        private static async Task<TempWorkspace> CreateMultiFileCoreAsync(params (string FileName, string Source)[] files)
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

            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? firstPath = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePaths[fileName] = path;
                firstPath ??= path;
            }

            firstPath ??= Path.Combine(directory, "Foo.cs");

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(firstPath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {firstPath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = firstPath,
                    SourcePaths = sourcePaths,
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

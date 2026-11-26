using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3130: <c>get_type_hierarchy</c> <c>direction</c> and <c>get_diagnostics</c>
/// <c>severityFilter</c> accept only defined member names (trimmed, case-insensitive). Numeric strings
/// and comma-separated combinations used to pass <c>Enum.TryParse</c> and silently return wrong results.
/// </summary>
public class StrictEnumParamsTests
{
    private const string Source = """
        interface IShape {}
        class Base {}
        class Mid : Base, IShape {}
        class Leaf : Mid {}
        class C
        {
            void M()
            {
        #warning One
                int x = Missing;
            }
        }
        """;

    [SkippableTheory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Ancestors, Descendants")]
    [InlineData("Descendants,Both")]
    public async Task GetTypeHierarchy_NumericOrCombinedDirection_IsRejected(string direction)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new GetTypeHierarchyOperation(workspace.Context).ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Mid",
                Direction = direction
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("Invalid direction", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ancestors", ex.Message, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("ancestors", true, false)]
    [InlineData("  Both  ", true, true)]
    [InlineData("DESCENDANTS", false, true)]
    [InlineData(null, true, true)]
    public async Task GetTypeHierarchy_DefinedDirectionNames_StillWork(string? direction, bool expectBase, bool expectDerived)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);

        var result = await new GetTypeHierarchyOperation(workspace.Context).ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mid",
            Direction = direction
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(expectBase ? new[] { "Base" } : Array.Empty<string>(), result.Data!.BaseTypes.Select(t => t.TypeName));
        Assert.Equal(expectDerived ? new[] { "Leaf" } : Array.Empty<string>(), result.Data.DerivedTypes.Select(t => t.TypeName));
    }

    [SkippableTheory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Error, Info")]
    [InlineData("Error,Hidden")]
    public async Task GetDiagnostics_NumericOrCombinedSeverityFilter_IsRejected(string severityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            new GetDiagnosticsOperation(workspace.Context).ExecuteAsync(new GetDiagnosticsParams
            {
                SourceFile = workspace.SourcePath,
                SeverityFilter = severityFilter
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("Invalid severityFilter", ex.Message, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("error", new[] { "CS0103" })]
    [InlineData(" ERROR ", new[] { "CS0103" })]
    [InlineData("Warning", new[] { "CS0103", "CS1030" })]
    public async Task GetDiagnostics_DefinedSeverityNames_StillWork(string severityFilter, string[] expectedIds)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);

        var result = await new GetDiagnosticsOperation(workspace.Context).ExecuteAsync(new GetDiagnosticsParams
        {
            SourceFile = workspace.SourcePath,
            SeverityFilter = severityFilter
        });

        Assert.True(result.Success, result.Error?.Message);
        var ids = result.Data!.Diagnostics.Select(d => d.Id).Where(id => id is "CS0103" or "CS1030").Distinct().OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(expectedIds, ids);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpStrictEnum_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, "Foo.cs");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

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

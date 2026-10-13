using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetSymbolInfoOperation"/> optional <c>maxResults</c>
/// and <c>kindFilter</c> (peer of <see cref="GetTypeHierarchyOperation"/> / <c>search_symbols</c>).
/// </summary>
public class GetSymbolInfoOperationTests
{
    private const string WidgetSource = """
        public interface IFirst { }
        public interface ISecond { }

        public class Widget : IFirst, ISecond
        {
            public const int Limit = 10;
            public int Alpha;
            public string Beta { get; set; } = "";
            public void Gamma() { }
            public int Delta(int a, string b, bool c) => a;
            public event System.EventHandler? Epsilon;
            public class Nested { }
        }
        """;

    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_MaxResultsBelowOne_ThrowsMissingRequiredParam(int maxResults)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                MaxResults = maxResults
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Members truncation

    [SkippableFact]
    public async Task GetSymbolInfo_OmittedMaxResults_ReturnsFullMembersNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_MaxResultsTruncates_ReturnsFirstNMembersAndSetsTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(full.Data.Members);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(full.Data.Members.Take(2).ToArray(), capped.Data.Members.ToArray());
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(7, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData(7)]
    [InlineData(50)]
    public async Task GetSymbolInfo_MaxResultsAtOrAboveCount_NotTruncated(int maxResults)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(full.Data.Members);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = maxResults
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(full.Data.Members.ToArray(), capped.Data.Members.ToArray());
        Assert.Equal(7, capped.Data.TotalCount);
        Assert.False(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_MaxResultsDoesNotCapInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Single(capped.Data.Members);
        Assert.True(capped.Data.Truncated);
        Assert.NotNull(capped.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, capped.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_MaxResultsHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            MaxResults = 1
        });

        Assert.True(full.Success);
        Assert.True(capped.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(capped.Data);
        Assert.Null(capped.Data.Members);
        Assert.Null(capped.Data.TotalCount);
        Assert.False(capped.Data.Truncated);
        Assert.NotNull(capped.Data.Parameters);
        Assert.Equal(3, capped.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, capped.Data.Signature);
        Assert.Equal(full.Data.ReturnType, capped.Data.ReturnType);
    }

    #endregion

    #region kindFilter

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSymbolInfo_OmittedOrBlankKindFilter_ReturnsFullMembers(string? kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterMethod_ReturnsOnlyMethods()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(2, result.Data.Members.Count);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(result.Data.Members, m => m.Contains("Gamma", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("Delta", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Alpha", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Beta", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("Property", "Beta")]
    [InlineData("Field", "Alpha")]
    [InlineData("Event", "Epsilon")]
    [InlineData("Constant", "Limit")]
    [InlineData("Class", "Nested")]
    public async Task GetSymbolInfo_KindFilter_MatchesMappedKind(string kindFilter, string expectedName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(expectedName, result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterCaseInsensitiveAndTrimmed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "  pRoPeRtY  "
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Contains("Beta", result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("Local")]
    [InlineData("Parameter")]
    [InlineData("Namespace")]
    [InlineData("Struct")]
    public async Task GetSymbolInfo_KindFilterNeverMatching_ReturnsEmptyMembers(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotNull(filtered.Data.Members);
        Assert.Equal(2, filtered.Data.Members.Count);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Single(capped.Data.Members);
        Assert.Equal(2, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(filtered.Data.Members[0], capped.Data.Members[0]);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterDoesNotFilterInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, result.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_KindFilterHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            KindFilter = "Property"
        });

        Assert.True(full.Success);
        Assert.True(filtered.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(filtered.Data);
        Assert.Null(filtered.Data.Members);
        Assert.Null(filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.NotNull(filtered.Data.Parameters);
        Assert.Equal(3, filtered.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, filtered.Data.Signature);
    }

    [SkippableTheory]
    [InlineData("Nope")]
    [InlineData("6")]
    [InlineData("Class, Method")]
    public async Task Validate_InvalidKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("kindFilter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetSymbolInfo_" + Guid.NewGuid().ToString("N"));
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

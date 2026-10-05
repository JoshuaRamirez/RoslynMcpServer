using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindReferencesOperation"/> optional <c>includeDeclaration</c>
/// (LSP <c>ReferenceContext.includeDeclaration</c> peer).
/// </summary>
public class FindReferencesOperationTests
{
    // Three partial parts of Widget (3 definition locations) plus two type usages in Consumer.
    // Usages are parameter types (no object creation) so the implicit constructor does not cascade.
    private const string PartialWidgetSource = """
        partial class Widget { public int A; }
        partial class Widget { public int B; }
        partial class Widget { public int C; }
        class Consumer
        {
            void Use(Widget first, Widget second) { }
        }
        """;

    // Position of "Widget" in the first partial part (line 1, column 15).
    private const int WidgetLine = 1;
    private const int WidgetColumn = 15;

    [Fact]
    public void IncludeDeclaration_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.IncludeDeclaration);
    }

    [SkippableFact]
    public async Task FindReferences_OmittedIncludeDeclaration_IncludesDefinitions()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // Roslyn cascades a type search to its implicit constructor, whose locations are the
        // partial parts too, so assert "at least the three parts" rather than an exact count.
        Assert.True(result.Data.References.Count(r => r.IsDefinition) >= 3);
        Assert.Equal(2, result.Data.References.Count(r => !r.IsDefinition));
        Assert.Equal(result.Data.References.Count, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationTrue_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn
        });
        var explicitTrue = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = true
        });

        Assert.True(omitted.Success);
        Assert.True(explicitTrue.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(explicitTrue.Data);
        Assert.Equal(omitted.Data.TotalCount, explicitTrue.Data.TotalCount);
        Assert.Equal(
            omitted.Data.References.Select(r => (r.File, r.Line, r.Column, r.IsDefinition)),
            explicitTrue.Data.References.Select(r => (r.File, r.Line, r.Column, r.IsDefinition)));
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalse_ReturnsUsagesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.DoesNotContain(result.Data.References, r => r.IsDefinition);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.Equal(6, r.Line));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalseWithMaxResults_CapsUsagesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = false,
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.References);
        Assert.False(only.IsDefinition);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalse_UnusedSymbol_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Lonely
            {
                public void Unused() { }
            }
            """);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Unused",
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindReferences_" + Guid.NewGuid().ToString("N"));
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

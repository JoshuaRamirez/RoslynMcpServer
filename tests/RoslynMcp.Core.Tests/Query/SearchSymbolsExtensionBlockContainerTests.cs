using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3047: <c>search_symbols</c> reported <c>containerName: ""</c> for every member
/// of a C# 14 <c>extension(...)</c> block (its containing type is the compiler's nameless extension
/// grouping type); the declaring static class is reported instead.
/// </summary>
public class SearchSymbolsExtensionBlockContainerTests
{
    private const string Source = """
        namespace App;

        public static class TagExtensions
        {
            extension(string tag)
            {
                public string Shout() => tag.ToUpperInvariant() + "!";
                public bool IsBlank => string.IsNullOrWhiteSpace(tag);
                public static string Banner(string s) => "[" + s + "]";
            }

            extension<T>(T[] items)
            {
                public int Twice() => items.Length * 2;
            }

            public static string Classic(this int n) => n.ToString();
        }

        public class User
        {
            public string Run() => "x".Shout() + "y".IsBlank + string.Banner("z") + new[] { 1 }.Twice() + 3.Classic();
        }
        """;

    [SkippableTheory]
    [InlineData("Shout")]
    [InlineData("IsBlank")]
    [InlineData("Banner")]
    [InlineData("Twice")]
    [InlineData("Classic")]
    public async Task SearchSymbols_ExtensionBlockMember_ReportsDeclaringStaticClassAsContainer(string query)
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);

        var result = await new SearchSymbolsOperation(workspace.Context).ExecuteAsync(new SearchSymbolsParams
        {
            Query = query,
            ExactMatch = true
        });

        Assert.True(result.Success, result.Error?.Message);
        var symbol = Assert.Single(result.Data!.Symbols);
        Assert.Equal(query, symbol.Name);
        Assert.Equal("TagExtensions", symbol.ContainerName);
    }

    [SkippableFact]
    public async Task SearchSymbols_OrdinaryMembers_KeepTypeAndNamespaceContainers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var run = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Run", ExactMatch = true });
        var user = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "User", ExactMatch = true });

        Assert.Equal("User", Assert.Single(run.Data!.Symbols).ContainerName);
        Assert.Equal("App", Assert.Single(user.Data!.Symbols).ContainerName);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchExtBlock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Fx.csproj");
            var sourcePath = Path.Combine(directory, "TagExtensions.cs");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <OutputType>Library</OutputType>
                    <LangVersion>preview</LangVersion>
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

                return new TempWorkspace { DirectoryPath = directory, Context = context };
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

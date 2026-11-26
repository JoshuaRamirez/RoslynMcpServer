using Microsoft.CodeAnalysis;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Regression tests for #3046: get_symbol_info Members left out every C# 14 extension-block member
/// of a static class, because the nameless extension block type was filtered out.
/// </summary>
public class GetSymbolInfoExtensionBlockMembersTests
{
    private const string Source = """
        namespace App;

        public static class TagExtensions
        {
            extension(string tag)
            {
                public string Shout() => tag.ToUpperInvariant();
                public bool IsBlank => tag.Length == 0;
                public static string Banner() => "banner";
            }

            extension<T>(T[] items)
            {
                public int Twice() => items.Length * 2;
            }

            public static string Classic(this string tag, int n) => tag + n;
        }
        """;

    [SkippableFact]
    public async Task Members_IncludeExtensionBlockMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "preview");

        var result = await Run(workspace, new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = "TagExtensions" });

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(5, result.Members!.Count);
        foreach (var name in new[] { "Shout", "IsBlank", "Banner", "Twice", "Classic" })
            Assert.Contains(result.Members, m => m.Contains(name, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Members, string.IsNullOrWhiteSpace);
    }

    [SkippableFact]
    public async Task NameFilter_FindsExtensionBlockMember()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "preview");

        var result = await Run(workspace, new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = "TagExtensions", NameFilter = "Shout" });

        Assert.Equal(1, result.TotalCount);
        Assert.Contains("Shout", Assert.Single(result.Members!), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task StaticFilter_IncludesStaticExtensionBlockMember()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "preview");

        var result = await Run(workspace, new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = "TagExtensions", StaticFilter = true });

        Assert.Contains(result.Members!, m => m.Contains("Banner", StringComparison.Ordinal));
        Assert.Contains(result.Members!, m => m.Contains("Classic", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Members!, m => m.Contains("Shout", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task KindFilterMethod_IncludesExtensionBlockMethods()
    {
        await using var workspace = await TempWorkspace.CreateAsync(Source, "preview");

        var result = await Run(workspace, new GetSymbolInfoParams { SourceFile = workspace.SourcePath, SymbolName = "TagExtensions", KindFilter = "Method" });

        Assert.Equal(4, result.TotalCount);
        Assert.DoesNotContain(result.Members!, m => m.Contains("IsBlank", StringComparison.Ordinal));
    }

    private static async Task<DetailedSymbolInfo> Run(TempWorkspace workspace, GetSymbolInfoParams @params)
    {
        var result = await new GetSymbolInfoOperation(workspace.Context).ExecuteAsync(@params);
        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        return result.Data;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string langVersion)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpLangVersion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Case.csproj");
            var sourcePath = Path.Combine(directory, "Program.cs");
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <LangVersion>{langVersion}</LangVersion>
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

        public async Task<List<string>> GetErrorsAsync()
        {
            var document = Context.GetDocumentByPath(SourcePath);
            Assert.NotNull(document);
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            return compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
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

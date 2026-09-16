using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Workspace;

public class WorkspaceContextPhysicalPathTests
{
    [SkippableFact]
    public async Task GetDocumentByPath_UsesPhysicalPathComparisonKey()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class C {}");
        var query = Path.Combine(Path.GetDirectoryName(workspace.SourcePath)!, FlipAsciiCase(Path.GetFileName(workspace.SourcePath)));
        var result = workspace.Context.GetDocumentByPath(query);

        if (File.Exists(query))
            Assert.NotNull(result);
        else
            Assert.Null(result);
    }

    [Fact]
    public void PathComparisonKey_CaseVariantExistingPaths_CollapseOnlyWhenFilesystemDoes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), "roslyn-mcp-wc-key-" + Path.GetRandomFileName() + ".cs");
        File.WriteAllText(filePath, "class C {}");

        try
        {
            var query = Path.Combine(Path.GetDirectoryName(filePath)!, FlipAsciiCase(Path.GetFileName(filePath)));
            var set = new HashSet<string>(StringComparer.Ordinal)
            {
                PathResolver.GetPathComparisonKey(filePath)
            };

            var addedSecond = set.Add(PathResolver.GetPathComparisonKey(query));
            Assert.Equal(!File.Exists(query), addedSecond);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void PathComparisonKey_IdenticalPaths_AlwaysDeduped()
    {
        var path = Path.Combine(Path.GetTempPath(), "roslyn-mcp-wc-same-" + Path.GetRandomFileName() + ".cs");
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            PathResolver.GetPathComparisonKey(path)
        };

        Assert.False(set.Add(PathResolver.GetPathComparisonKey(path)));
        Assert.Single(set);
    }

    private static string FlipAsciiCase(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z')
                chars[i] = char.ToUpperInvariant(chars[i]);
            else if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpWorkspaceContext_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePath = Path.Combine(directory, "FileA.cs");

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(sourcePath, source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = sourcePath,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not Xunit.SkipException)
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

            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                await Task.Yield();
            }
        }
    }
}

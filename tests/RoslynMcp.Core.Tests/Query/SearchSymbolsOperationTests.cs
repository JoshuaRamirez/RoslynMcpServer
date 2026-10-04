using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="SearchSymbolsOperation"/> optional <c>sourceFile</c>
/// filtering (peer of <see cref="GetDiagnosticsOperation"/>).
/// </summary>
public class SearchSymbolsOperationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    #region Input Validation

    [Fact]
    public void SourceFile_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.SourceFile);
    }

    [SkippableFact]
    public async Task Validate_RelativeSourceFile_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = "file.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Validate_NonCsSourceFile_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = AbsoluteTestPath(".txt")
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Validate_MissingSourceFile_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Execute filtering

    [SkippableFact]
    public async Task SearchSymbols_OmittedSourceFile_ReturnsMatchesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Unique"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueBeta");
        Assert.True(result.Data.TotalCount >= 2);
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_LimitsToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Unique",
            SourceFile = alphaPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
        Assert.DoesNotContain(result.Data.Symbols, s => s.Name == "UniqueBeta");
        Assert.All(result.Data.Symbols, s =>
            Assert.True(
                string.Equals(s.File, alphaPath, StringComparison.OrdinalIgnoreCase),
                $"Expected file {alphaPath}, got {s.File}"));
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_CaseInsensitivePathMatch()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var flipped = FlipAsciiCase(Path.GetFileName(alphaPath));
        var queryPath = Path.Combine(Path.GetDirectoryName(alphaPath)!, flipped);
        // File.Exists must succeed; on case-sensitive FS the flipped path may not exist.
        // Only run the case-flip path when the OS resolves it to the same file.
        if (!File.Exists(queryPath))
        {
            queryPath = alphaPath;
        }

        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "UniqueAlpha",
            SourceFile = queryPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
    }

    #endregion

    private static string FlipAsciiCase(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                break;
            }
        }
        return new string(chars);
    }

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbols_" + Guid.NewGuid().ToString("N"));
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

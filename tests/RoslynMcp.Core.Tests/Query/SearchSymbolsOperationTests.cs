using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="SearchSymbolsOperation"/> optional <c>sourceFile</c>
/// filtering (peer of <see cref="GetDiagnosticsOperation"/>) and optional <c>caseSensitive</c>
/// name matching.
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
    public async Task SearchSymbols_WithSourceFile_MatchesPartialDeclaredInRequestedFile()
    {
        // First location is PartA; request PartB so FirstOrDefault-only would wrongly miss.
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("PartA.cs", "partial class UniquePartial { public void A() { } }\n"),
            ("PartB.cs", "partial class UniquePartial { public void B() { } }\n"));

        var partB = workspace.SourcePaths["PartB.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "UniquePartial",
            SourceFile = partB
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniquePartial");
        Assert.All(result.Data.Symbols.Where(s => s.Name == "UniquePartial"), s =>
            Assert.True(
                string.Equals(s.File, partB, StringComparison.OrdinalIgnoreCase),
                $"Expected file {partB}, got {s.File}"));
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

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_ExistingOutsideWorkspace_ReturnsSuccessEmpty()
    {
        // Existing absolute .cs outside the loaded workspace is valid: File.Exists
        // passes and path filtering yields no declarations (not SourceNotInWorkspace).
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbols_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class OutsideUnique { }\n");

            var operation = new SearchSymbolsOperation(workspace.Context);
            var result = await operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = outsidePath
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data.Symbols);
            Assert.Equal(0, result.Data.TotalCount);
        }
        finally
        {
            try
            {
                Directory.Delete(outsideDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    #endregion

    #region caseSensitive

    // Mixed-case names: case-insensitive "ID" matches all of Ids/Id/ID/Identity/Validate/Width/IDGenerator;
    // case-sensitive "ID" matches only ID and IDGenerator.
    private const string MixedCaseSource = """
        class Ids
        {
            public int Id;
            public int ID;
            public int Identity;
            public void Validate() { }
            public int Width;
        }
        class IDGenerator { }
        """;

    private static readonly string[] CaseInsensitiveIdNames =
        ["Ids", "Id", "ID", "Identity", "Validate", "Width", "IDGenerator"];

    [Fact]
    public void CaseSensitive_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.CaseSensitive);
    }

    [SkippableFact]
    public async Task SearchSymbols_OmittedCaseSensitive_MatchesCaseInsensitively()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID" });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var names = result.Data.Symbols.Select(s => s.Name).ToList();
        Assert.All(CaseInsensitiveIdNames, n => Assert.Contains(n, names));
        Assert.Equal(CaseInsensitiveIdNames.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveFalse_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID" });
        var explicitFalse = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID", CaseSensitive = false });

        Assert.True(omitted.Success);
        Assert.True(explicitFalse.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(explicitFalse.Data);
        Assert.Equal(omitted.Data.TotalCount, explicitFalse.Data.TotalCount);
        Assert.Equal(omitted.Data.Truncated, explicitFalse.Data.Truncated);
        Assert.Equal(
            omitted.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)),
            explicitFalse.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)));
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_MatchesExactCaseOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID", CaseSensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[] { "ID", "IDGenerator" },
            result.Data.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_LowercaseQuery_MatchesLowercaseSubstringOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "id", CaseSensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[] { "Validate", "Width" },
            result.Data.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithKindFilter_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var fields = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            KindFilter = "Field"
        });
        var classes = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            KindFilter = "Class"
        });

        Assert.True(fields.Success);
        Assert.NotNull(fields.Data);
        var field = Assert.Single(fields.Data.Symbols);
        Assert.Equal("ID", field.Name);
        Assert.Equal(1, fields.Data.TotalCount);

        Assert.True(classes.Success);
        Assert.NotNull(classes.Data);
        var type = Assert.Single(classes.Data.Symbols);
        Assert.Equal("IDGenerator", type.Name);
        Assert.Equal(1, classes.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithMaxResults_TotalCountReflectsCaseSensitiveSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var sensitive = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            MaxResults = 1
        });
        var insensitive = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            MaxResults = 1
        });

        Assert.True(sensitive.Success);
        Assert.NotNull(sensitive.Data);
        var only = Assert.Single(sensitive.Data.Symbols);
        Assert.Contains(only.Name, new[] { "ID", "IDGenerator" });
        Assert.Equal(2, sensitive.Data.TotalCount);
        Assert.True(sensitive.Data.Truncated);

        Assert.True(insensitive.Success);
        Assert.NotNull(insensitive.Data);
        Assert.Single(insensitive.Data.Symbols);
        Assert.Equal(CaseInsensitiveIdNames.Length, insensitive.Data.TotalCount);
        Assert.True(insensitive.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithSourceFile_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class IDAlpha { public int Id; }\n"),
            ("Beta.cs", "class IDBeta { public int ID; }\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            SourceFile = alphaPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Symbols);
        Assert.Equal("IDAlpha", only.Name);
        Assert.True(
            string.Equals(only.File, alphaPath, StringComparison.OrdinalIgnoreCase),
            $"Expected file {alphaPath}, got {only.File}");
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
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

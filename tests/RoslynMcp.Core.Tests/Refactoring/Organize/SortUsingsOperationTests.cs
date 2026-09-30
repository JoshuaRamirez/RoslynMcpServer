using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Operation-level tests for <see cref="SortUsingsOperation"/>, including <c>systemFirst</c> and <c>allFiles</c>.
/// </summary>
public class SortUsingsOperationTests
{
    private const string UnsortedSource = """
        using MyApp.Services;
        using System.Collections.Generic;
        using ThirdParty;
        using System;
        using static MyApp.Helpers;
        using static System.Math;
        using Z = MyApp.Zeta;
        using A = System.IO;

        namespace TestApp;

        public class Foo
        {
        }
        """;

    [SkippableFact]
    public async Task SortUsings_DefaultSystemFirst_PlacesSystemNamespacesFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(
            new[]
            {
                "System",
                "System.Collections.Generic",
                "MyApp.Services",
                "ThirdParty",
                "static System.Math",
                "static MyApp.Helpers",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_SystemFirstTrue_PlacesSystemNamespacesFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = true
        });

        Assert.True(result.Success);
        Assert.Equal(
            new[]
            {
                "System",
                "System.Collections.Generic",
                "MyApp.Services",
                "ThirdParty",
                "static System.Math",
                "static MyApp.Helpers",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_SystemFirstFalse_SortsAlphabeticallyWithinGroups()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(
            new[]
            {
                "MyApp.Services",
                "System",
                "System.Collections.Generic",
                "ThirdParty",
                "static MyApp.Helpers",
                "static System.Math",
                "A = System.IO",
                "Z = MyApp.Zeta"
            },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePath)));
    }

    [SkippableFact]
    public async Task SortUsings_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("MyApp.Services", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("System", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SortUsings_PreviewSystemFirstTrue_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePath,
            SystemFirst = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesFalse_SortsOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB),
            ("AlreadySorted.cs", AlreadySorted));
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]);
        var beforeSorted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            SourceFile = workspace.SourcePaths["UnsortedA.cs"],
            AllFiles = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(new[] { "System", "MyApp.Services" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"])));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]));
        Assert.Equal(beforeSorted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedA.cs"]));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_WithoutSourceFile_SortsMultipleUnsortedFiles_LeavesAlreadySortedUntouched()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB),
            ("AlreadySorted.cs", AlreadySorted));
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeSorted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Equal(new[] { "System", "MyApp.Services" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"])));
        Assert.Equal(new[] { "System", "System.Linq", "ThirdParty" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"])));
        Assert.Equal(beforeSorted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AlreadySorted.cs"]));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnsortedSource);
        var operation = new SortUsingsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SortUsingsParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SortUsings_PreviewAllFiles_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB),
            ("AlreadySorted.cs", AlreadySorted));
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]);
        var beforeSorted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnsortedA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnsortedB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["AlreadySorted.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]));
        Assert.Equal(beforeSorted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]));
    }


    #region AllFiles optional sourceFile

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB),
            ("AlreadySorted.cs", AlreadySorted));
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]);
        var beforeSorted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnsortedA.cs"]
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "System", "MyApp.Services" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"])));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]));
        Assert.Equal(beforeSorted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedA.cs"]));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB),
            ("AlreadySorted.cs", AlreadySorted));
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]);
        var beforeSorted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["UnsortedA.cs"]);

        // Shared AllFilesDocumentHelpers default path returns ignore-case workspace
        // matches even when File.Exists(flipped) is false (case-sensitive volumes).
        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "System", "MyApp.Services" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"])));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedB.cs"]));
        Assert.Equal(beforeSorted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadySorted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedA.cs"]));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB));
        var operation = new SortUsingsOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpSortUsings_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "using MyApp.Services;\nusing System;\nclass Outside { }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new SortUsingsParams
                {
                    AllFiles = true,
                    SourceFile = outsidePath
                }));

            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_MissingPath_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA),
            ("UnsortedB.cs", UnsortedB));
        var operation = new SortUsingsOperation(workspace.Context);
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpSortUsings_Missing_" + Guid.NewGuid().ToString("N"), "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SortUsingsParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnsortedA.cs", UnsortedA), ("unsorteda.cs", UnsortedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnsortedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unsorteda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new SortUsingsOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["unsorteda.cs"]);

        var result = await operation.ExecuteAsync(new SortUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnsortedA.cs"]
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "System", "MyApp.Services" },
            GetUsingKeys(await File.ReadAllTextAsync(workspace.SourcePaths["UnsortedA.cs"])));
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["unsorteda.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnsortedA.cs"]));
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnsortedA.cs", UnsortedA), ("unsorteda.cs", UnsortedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnsortedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unsorteda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new SortUsingsOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["UnsortedA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SortUsingsParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_RelativePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA));
        var operation = new SortUsingsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SortUsingsParams
            {
                AllFiles = true,
                SourceFile = "UnsortedA.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SortUsings_AllFilesTrue_OptionalSourceFile_NonCSharpAbsolutePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnsortedA.cs", UnsortedA));
        var operation = new SortUsingsOperation(workspace.Context);
        var nonCs = Path.Combine(Path.GetTempPath(), "RoslynMcpSortUsings_NonCs_" + Guid.NewGuid().ToString("N") + ".txt");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SortUsingsParams
            {
                AllFiles = true,
                SourceFile = nonCs
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    #endregion

    private const string UnsortedA = """
        using MyApp.Services;
        using System;

        namespace TestApp;

        public class UnsortedA
        {
        }
        """;

    private const string UnsortedB = """
        using ThirdParty;
        using System.Linq;
        using System;

        namespace TestApp;

        public class UnsortedB
        {
        }
        """;

    private const string AlreadySorted = """
        using System;
        using MyApp.Services;

        namespace TestApp;

        public class AlreadySorted
        {
        }
        """;

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string FlipPathCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i])
                    ? char.ToLowerInvariant(chars[i])
                    : char.ToUpperInvariant(chars[i]);
                break;
            }
        }

        return new string(chars);
    }

    private static List<string> GetUsingKeys(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        return root.Usings.Select(ToUsingKey).ToList();
    }

    private static string ToUsingKey(UsingDirectiveSyntax usingDirective)
    {
        var name = usingDirective.Name?.ToString() ?? string.Empty;
        if (usingDirective.Alias != null)
            return $"{usingDirective.Alias.Name} = {name}";
        if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            return $"static {name}";
        return name;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateWithFilesAsync([(fileName, source)]);

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateWithFilesAsync(files, explicitCompileItems: false);

        public static async Task<TempWorkspace> CreateWithFilesAsync(
            IReadOnlyList<(string FileName, string Source)> files,
            bool explicitCompileItems)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSortUsings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);

            var compileItems = explicitCompileItems
                ? string.Join(Environment.NewLine, files.Select(f => $"    <Compile Include=\"{f.FileName}\" />"))
                : string.Empty;

            // Pin authored sources so generated AssemblyInfo / TFM attributes
            // are not hit by the allFiles .cs document walk.
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                {(explicitCompileItems ? "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>" : string.Empty)}
                  </PropertyGroup>
                {(explicitCompileItems ? $"  <ItemGroup>{Environment.NewLine}{compileItems}{Environment.NewLine}  </ItemGroup>" : string.Empty)}
                </Project>
                """);

            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = sourcePaths.Values.First(),
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

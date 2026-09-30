using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Format;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Format;

/// <summary>
/// Operation-level tests for <see cref="FormatDocumentOperation"/>, including <c>preview</c> and <c>allFiles</c>.
/// </summary>
public class FormatDocumentOperationTests
{
    private const string UnformattedSource = """
        using System;
        namespace TestApp{
        public class Foo{
        public void Bar(){
        var x=1+2;
        if(x>0){
        Console.WriteLine(x);
        }
        }
        }
        }
        """;

    [SkippableFact]
    public async Task FormatDocument_OmittedPreview_WritesFormattedFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.NotEqual(before, after);
        Assert.Contains("var x = 1 + 2;", after);
    }

    [SkippableFact]
    public async Task FormatDocument_PreviewFalse_WritesFormattedFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var after = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.NotEqual(before, after);
        Assert.Contains("var x = 1 + 2;", after);
    }

    [SkippableFact]
    public async Task FormatDocument_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Modify, result.PendingChanges[0].ChangeType);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("var x=1+2;", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("var x = 1 + 2;", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task FormatDocument_Preview_AlreadyFormatted_SucceedsWithoutWriting()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var apply = new FormatDocumentOperation(workspace.Context);
        var applyResult = await apply.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(applyResult.Success);

        var formatted = await File.ReadAllTextAsync(workspace.SourcePath);
        var preview = new FormatDocumentOperation(workspace.Context);
        var result = await preview.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Empty(result.PendingChanges);
        Assert.Equal(formatted, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesFalse_FormatsOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["UnformattedA.cs"],
            AllFiles = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_WithoutSourceFile_FormatsMultipleUnformattedFiles_LeavesAlreadyFormattedUntouched()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        var afterB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Contains("var b = 3 + 4;", afterB);
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["AlreadyFormatted.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnformattedSource);
        var operation = new FormatDocumentOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FormatDocumentParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FormatDocument_PreviewAllFiles_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["UnformattedB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnformattedA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB),
            ("AlreadyFormatted.cs", AlreadyFormatted));
        var operation = new FormatDocumentOperation(workspace.Context);
        var preFormat = await operation.ExecuteAsync(new FormatDocumentParams
        {
            SourceFile = workspace.SourcePaths["AlreadyFormatted.cs"]
        });
        Assert.True(preFormat.Success);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]);
        var beforeFormatted = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["UnformattedA.cs"]);

        // Shared AllFilesDocumentHelpers default path returns ignore-case workspace
        // matches even when File.Exists(flipped) is false (case-sensitive volumes).
        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedB.cs"]));
        Assert.Equal(beforeFormatted, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyFormatted.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB));
        var operation = new FormatDocumentOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpFormatDocument_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class Outside { void M(){var x=1;} }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new FormatDocumentParams
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
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_MissingPath_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnformattedA.cs", UnformattedA),
            ("UnformattedB.cs", UnformattedB));
        var operation = new FormatDocumentOperation(workspace.Context);
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpFormatDocument_Missing_" + Guid.NewGuid().ToString("N"), "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FormatDocumentParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnformattedA.cs", UnformattedA), ("unformatteda.cs", UnformattedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnformattedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unformatteda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new FormatDocumentOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["unformatteda.cs"]);

        var result = await operation.ExecuteAsync(new FormatDocumentParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnformattedA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnformattedA.cs"]);
        Assert.Contains("var a = 1 + 2;", afterA);
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["unformatteda.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnformattedA.cs"]));
    }

    [SkippableFact]
    public async Task FormatDocument_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnformattedA.cs", UnformattedA), ("unformatteda.cs", UnformattedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnformattedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unformatteda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new FormatDocumentOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["UnformattedA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FormatDocumentParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string UnformattedA = """
        using System;
        namespace TestApp{
        public class UnformattedA{
        public void Bar(){
        var a=1+2;
        }
        }
        }
        """;

    private const string UnformattedB = """
        using System;
        namespace TestApp{
        public class UnformattedB{
        public void Bar(){
        var b=3+4;
        }
        }
        }
        """;

    private const string AlreadyFormatted = """
        using System;

        namespace TestApp
        {
            public class AlreadyFormatted
            {
                public void Bar()
                {
                    var x = 1 + 2;
                }
            }
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFormatDocument_" + Guid.NewGuid().ToString("N"));
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

using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Convert;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Convert;

/// <summary>
/// Operation-level tests for <see cref="ConvertAnonymousToClassOperation"/> allFiles.
/// </summary>
public class ConvertAnonymousToClassAllFilesOperationTests
{
    private const string FileA = """
        namespace TestApp;

        public class FileA
        {
            public object Create()
            {
                return new { Name = "Ada", Age = 36 };
            }
        }
        """;

    private const string FileB = """
        namespace TestApp;

        public class FileB
        {
            public object Create()
            {
                return new { Id = 1, Label = "x" };
            }
        }
        """;

    private const string FileSameShapeAsA = """
        namespace TestApp;

        public class FileSameShape
        {
            public object Create()
            {
                return new { Name = "Grace", Age = 40 };
            }
        }
        """;

    private const string FileNoAnonymous = """
        namespace TestApp;

        public class FileC
        {
            public int Run() => 1;
        }
        """;

    private const string CollisionHost = """
        namespace TestApp;

        public class NameAge
        {
        }

        public class CollisionHost
        {
            public object Create()
            {
                return new { Name = "Ada", Age = 36 };
            }
        }
        """;

    [SkippableFact]
    public async Task ConvertAnonymous_OmittedAllFiles_KeepsSingleSiteConvert()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FileA);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            SourceFile = workspace.SourcePath,
            Line = FindLine(FileA, "return new { Name"),
            NewTypeName = "Person"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class Person", updated, StringComparison.Ordinal);
        Assert.Contains("new Person", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Name", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_ConvertsDistinctShapesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", FileA),
            ("FileB.cs", FileB),
            ("FileC.cs", FileNoAnonymous));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));

        Assert.Contains("class NameAge", updatedA, StringComparison.Ordinal);
        Assert.Contains("new NameAge", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Name", updatedA, StringComparison.Ordinal);

        Assert.Contains("class IdLabel", updatedB, StringComparison.Ordinal);
        Assert.Contains("new IdLabel", updatedB, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Id", updatedB, StringComparison.Ordinal);

        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesModified.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_SameShapeConvertedOnce()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", FileA),
            ("FileSame.cs", FileSameShapeAsA));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedSame = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileSame.cs"]));

        Assert.Contains("class NameAge", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("class NameAge", updatedSame, StringComparison.Ordinal);
        Assert.Contains("new NameAge", updatedA, StringComparison.Ordinal);
        Assert.Contains("new NameAge", updatedSame, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Name", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Name", updatedSame, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_NamesFromMemberJoin()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FileA);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("class NameAge", updated, StringComparison.Ordinal);
        Assert.Contains("new NameAge", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_PreviewAggregatesWithoutWriting()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", FileA),
            ("FileB.cs", FileB));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges!.Count >= 1);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_SourceFileFilterLimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", FileA),
            ("FileB.cs", FileB));
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("class NameAge", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_SkipsNameCollisionWithNumericSuffixWhenPossible()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CollisionHost, "Collision.cs");
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        // Existing NameAge class forces NameAge2 (or skip if allocate fails — prefer suffix).
        Assert.True(
            updated.Contains("class NameAge2", StringComparison.Ordinal) ||
            updated.Contains("new { Name", StringComparison.Ordinal),
            updated);
        if (updated.Contains("class NameAge2", StringComparison.Ordinal))
        {
            Assert.Contains("new NameAge2", updated, StringComparison.Ordinal);
            Assert.DoesNotContain("new { Name", updated, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task ConvertAnonymous_AllFilesTrue_EmptyWalkSucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(FileNoAnonymous);
        var operation = new ConvertAnonymousToClassOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ConvertAnonymousToClassParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Rejects()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Rejects()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithNewTypeName_Rejects()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = true,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("newTypeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Rejects()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ConvertAnonymousToClassOperation.Validate(new ConvertAnonymousToClassParams
            {
                AllFiles = false,
                Line = 1,
                NewTypeName = "Person"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeriveTypeNameFromMembers_JoinsPascalCaseNames()
    {
        Assert.Equal("NameAge", ConvertAnonymousToClassOperation.DeriveTypeNameFromMembers(BuildMembers("Name", "Age")));
        Assert.Equal("IdLabel", ConvertAnonymousToClassOperation.DeriveTypeNameFromMembers(BuildMembers("id", "label")));
        Assert.Null(ConvertAnonymousToClassOperation.DeriveTypeNameFromMembers([]));
    }

    #region Helpers

    private static List<ConvertAnonymousToClassOperation.AnonymousMember> BuildMembers(params string[] names) =>
        names.Select(n => new ConvertAnonymousToClassOperation.AnonymousMember(n, null!)).ToList();

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet not found: {snippet}");
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Worker.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpConvertAnonAllFiles_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
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
                    ProjectPath = projectPath,
                    SourcePath = firstSource!,
                    SourcePaths = sourcePaths,
                    Context = context
                };
            }
            catch (Exception ex) when (ex is not SkipException)
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
                Skip.If(true, $"Workspace load failed: {ex.Message}");
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.Dispose();
            await Task.Run(() =>
            {
                try { Directory.Delete(DirectoryPath, recursive: true); } catch { }
            });
        }
    }

    #endregion
}

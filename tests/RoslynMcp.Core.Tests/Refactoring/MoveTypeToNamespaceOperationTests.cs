using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="MoveTypeToNamespaceOperation"/>,
/// including <c>updateFileLocation</c>.
/// </summary>
public class MoveTypeToNamespaceOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    #region Destination Path

    [Fact]
    public void ComputeDestinationFile_RemapsMatchingNamespaceFolder()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveNsProj");
        var source = Path.Combine(projectDir, "Old", "Ns", "Foo.cs");

        var dest = MoveTypeToNamespaceOperation.ComputeDestinationFile(
            source,
            "Old.Ns",
            "New.Ns",
            projectDir);

        Assert.Equal(
            PathResolver.NormalizePath(Path.Combine(projectDir, "New", "Ns", "Foo.cs")),
            dest);
    }

    [Fact]
    public void ComputeDestinationFile_UsesNamespacePathUnderProjectWhenUnmatched()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveNsRoot");
        var source = Path.Combine(projectDir, "Foo.cs");

        var dest = MoveTypeToNamespaceOperation.ComputeDestinationFile(
            source,
            "MyApp",
            "MyApp.Services",
            projectDir);

        Assert.Equal(
            PathResolver.NormalizePath(Path.Combine(projectDir, "MyApp", "Services", "Foo.cs")),
            dest);
    }

    [Fact]
    public void IsUnchangedFileLocation_CaseOnlyDistinctPathsWhenDestMissing_IsFalse()
    {
        var source = Path.Combine(Path.GetTempPath(), "Old", "Ns", "Foo.cs");
        var dest = Path.Combine(Path.GetTempPath(), "old", "ns", "Foo.cs");

        Assert.NotEqual(source, dest);
        Assert.Equal(source, dest, StringComparer.OrdinalIgnoreCase);
        Assert.False(MoveTypeToNamespaceOperation.IsUnchangedFileLocation(source, dest));
    }

    [Fact]
    public void CompileSpecCoversFile_MatchesGlobsAndLiterals()
    {
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var source = Path.Combine(projectDir, "Old", "Ns", "Foo.cs");
        var dest = Path.Combine(projectDir, "New", "Ns", "Foo.cs");

        Assert.True(MoveTypeToNamespaceOperation.CompileSpecCoversFile("Old/Ns/**/*.cs", projectDir, source));
        Assert.False(MoveTypeToNamespaceOperation.CompileSpecCoversFile("Old/Ns/**/*.cs", projectDir, dest));
        Assert.True(MoveTypeToNamespaceOperation.CompileSpecCoversFile("**/*.cs", projectDir, source));
        Assert.True(MoveTypeToNamespaceOperation.CompileSpecCoversFile("**/*.cs", projectDir, dest));
        Assert.True(MoveTypeToNamespaceOperation.CompileSpecCoversFile("Old/Ns/Foo.cs", projectDir, source));
        Assert.False(MoveTypeToNamespaceOperation.CompileSpecCoversFile("Old/Ns/Foo.cs", projectDir, dest));
    }

    [Fact]
    public void UpdateProjectTextForFileMove_SupplementsExplicitGlob()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Old/Ns/**/*.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var source = Path.Combine(projectDir, "Old", "Ns", "Foo.cs");
        var dest = Path.Combine(projectDir, "New", "Ns", "Foo.cs");

        var updated = MoveTypeToNamespaceOperation.UpdateProjectTextForFileMove(
            xml,
            projectDir,
            source,
            dest);

        Assert.Contains("Old/Ns/**/*.cs", updated);
        Assert.Contains("New/Ns/Foo.cs", updated);
    }

    [Fact]
    public void UpdateProjectTextForFileMove_RewritesSemicolonLiteralList()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Old/Ns/Foo.cs;Consumer.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var source = Path.Combine(projectDir, "Old", "Ns", "Foo.cs");
        var dest = Path.Combine(projectDir, "New", "Ns", "Foo.cs");

        var updated = MoveTypeToNamespaceOperation.UpdateProjectTextForFileMove(
            xml,
            projectDir,
            source,
            dest);

        Assert.Contains("New/Ns/Foo.cs;Consumer.cs", updated);
        Assert.DoesNotContain("Old/Ns/Foo.cs", updated);
    }

    #endregion

    #region updateFileLocation

    [SkippableFact]
    public async Task UpdateFileLocationFalse_LeavesFileAndRewritesNamespace()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;
        const string consumer = """
            using Old.Ns;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var oldFile = workspace.GetPath("Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = false
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(oldFile));
        Assert.False(File.Exists(newFile));
        Assert.False(Directory.Exists(workspace.GetPath("New/Ns")));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(oldFile));
        Assert.DoesNotContain("namespace Old.Ns;", await File.ReadAllTextAsync(oldFile));
        Assert.Contains("using New.Ns;", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.NotNull(workspace.Context.GetDocumentByPath(oldFile));
        Assert.Null(workspace.Context.GetDocumentByPath(newFile));
        Assert.DoesNotContain(newFile, result.Changes!.FilesCreated);
        Assert.DoesNotContain(oldFile, result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_MovesFileAndUpdatesNamespace()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;
        const string consumer = """
            using Old.Ns;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var oldFile = workspace.GetPath("Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.True(Directory.Exists(workspace.GetPath("New/Ns")));

        var declaration = await File.ReadAllTextAsync(newFile);
        Assert.Contains("namespace New.Ns;", declaration);
        Assert.DoesNotContain("namespace Old.Ns;", declaration);

        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("using New.Ns;", usages);
        Assert.Contains(newFile, result.Changes!.FilesCreated);
        Assert.Contains(oldFile, result.Changes.FilesDeleted);
        Assert.NotNull(workspace.Context.GetDocumentByPath(newFile));
        Assert.Null(workspace.Context.GetDocumentByPath(oldFile));
        Assert.Equal(newFile, result.Symbol!.NewLocation!.File);
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_Preview_WritesNothing()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;
        const string consumer = """
            using Old.Ns;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var oldFile = workspace.GetPath("Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.ChangeType == ChangeKind.Create &&
            string.Equals(
                Path.GetFullPath(c.File),
                Path.GetFullPath(newFile),
                StringComparison.OrdinalIgnoreCase));

        Assert.True(File.Exists(oldFile));
        Assert.False(File.Exists(newFile));
        Assert.True(Directory.Exists(workspace.GetPath("Old/Ns")));
        Assert.False(Directory.Exists(workspace.GetPath("New/Ns")));
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.Contains("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("using Old.Ns;", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_DestinationExists_ThrowsTargetFileExists()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Old/Ns/Foo.cs");
        var destFile = workspace.GetPath("New/Ns/Foo.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        await File.WriteAllTextAsync(destFile, "// occupied");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Foo",
                TargetNamespace = "New.Ns",
                UpdateFileLocation = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.True(Directory.Exists(workspace.GetPath("Old/Ns")));
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal("// occupied", await File.ReadAllTextAsync(destFile));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_ExistingDestinationFolder_MovesIntoIt()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Old/Ns/Foo.cs");
        var destFolder = workspace.GetPath("New/Ns");
        Directory.CreateDirectory(destFolder);
        var destFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(destFile));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_UnmatchedFolder_CreatesNamespacePathUnderProject()
    {
        const string source = """
            namespace MyApp;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var destFile = workspace.GetPath("MyApp/Services/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "MyApp.Services",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(destFile));
        Assert.False(File.Exists(workspace.GetPath("Foo.cs")));
        Assert.Contains("namespace MyApp.Services;", await File.ReadAllTextAsync(destFile));
        Assert.NotNull(workspace.Context.GetDocumentByPath(destFile));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_ExplicitCompileItem_UpdatesProjectFile()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;
        const string consumer = """
            using Old.Ns;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));

        var csproj = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.Contains("New/Ns/Foo.cs", csproj);
        Assert.DoesNotContain("Old/Ns/Foo.cs", csproj);
        Assert.Contains("Consumer.cs", csproj);
        Assert.Contains(workspace.ProjectPath, result.Changes!.FilesModified);

        workspace.Context.Dispose();
        var provider = new MSBuildWorkspaceProvider();
        using var reloaded = await provider.CreateContextAsync(workspace.ProjectPath);
        Assert.NotNull(reloaded.GetDocumentByPath(newFile));
        Assert.Null(reloaded.GetDocumentByPath(workspace.GetPath("Old/Ns/Foo.cs")));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_ReadOnlyProjectWithCompileItems_ThrowsDocumentNotEditable()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Old/Ns/Foo.cs", source));
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var destFile = workspace.GetPath("New/Ns/Foo.cs");
        var projectInfo = new FileInfo(workspace.ProjectPath);
        var wasReadOnly = projectInfo.IsReadOnly;
        projectInfo.IsReadOnly = true;
        try
        {
            var operation = new MoveTypeToNamespaceOperation(workspace.Context);
            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new MoveTypeToNamespaceParams
                {
                    SourceFile = workspace.SourcePath,
                    SymbolName = "Foo",
                    TargetNamespace = "New.Ns",
                    UpdateFileLocation = true
                }));

            Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
            Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
            Assert.False(File.Exists(destFile));
            Assert.False(Directory.Exists(workspace.GetPath("New/Ns")));
        }
        finally
        {
            projectInfo.IsReadOnly = wasReadOnly;
        }
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_ExplicitGlobCompileItem_SupplementsDestFile()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithCompileIncludesAsync(
            ["Old/Ns/**/*.cs"],
            ("Old/Ns/Foo.cs", source));
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));

        var csproj = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.Contains("Old/Ns/**/*.cs", csproj);
        Assert.Contains("New/Ns/Foo.cs", csproj);
        Assert.Contains(workspace.ProjectPath, result.Changes!.FilesModified);

        workspace.Context.Dispose();
        var provider = new MSBuildWorkspaceProvider();
        using var reloaded = await provider.CreateContextAsync(workspace.ProjectPath);
        Assert.NotNull(reloaded.GetDocumentByPath(newFile));
        Assert.Null(reloaded.GetDocumentByPath(workspace.GetPath("Old/Ns/Foo.cs")));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_LinkedFileInSecondProject_RemapsBothDocuments()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateLinkedFileSolutionAsync(
            ("Old/Ns/Foo.cs", source));
        var oldFile = workspace.GetPath("Owner/Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("Owner/New/Ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = oldFile,
            SymbolName = "Foo",
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(oldFile));

        var destDocs = workspace.Context.Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document =>
                document.FilePath != null
                && string.Equals(
                    PathResolver.NormalizePath(document.FilePath),
                    PathResolver.NormalizePath(newFile),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, destDocs.Count);

        var staleDocs = workspace.Context.Solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document =>
                document.FilePath != null
                && string.Equals(
                    PathResolver.NormalizePath(document.FilePath),
                    PathResolver.NormalizePath(oldFile),
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(staleDocs);

        var linkerCsproj = await File.ReadAllTextAsync(workspace.SecondaryProjectPath);
        Assert.Contains("New/Ns/Foo.cs", linkerCsproj);
        Assert.DoesNotContain("Old/Ns/Foo.cs", linkerCsproj);

        workspace.Context.Dispose();
        var provider = new MSBuildWorkspaceProvider();
        using var reloaded = await provider.CreateContextAsync(workspace.SolutionPath);
        Assert.NotNull(reloaded.GetDocumentByPath(newFile));
        Assert.Null(reloaded.GetDocumentByPath(oldFile));
        Assert.Equal(
            2,
            reloaded.Solution.Projects.SelectMany(p => p.Documents)
                .Count(d =>
                    d.FilePath != null
                    && string.Equals(
                        PathResolver.NormalizePath(d.FilePath),
                        PathResolver.NormalizePath(newFile),
                        StringComparison.OrdinalIgnoreCase)));
    }

    [SkippableFact]
    public async Task UpdateFileLocationTrue_CaseOnlyFolderChange_MovesOnCaseSensitiveFileSystem()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Old/Ns/Foo.cs");
        Skip.If(
            !IsCaseSensitiveDirectory(workspace.DirectoryPath),
            "Volume is case-insensitive; case-only paths are the same file.");

        var oldFile = workspace.GetPath("Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("old/ns/Foo.cs");
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Foo",
            TargetNamespace = "old.ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(oldFile));
        Assert.Contains("namespace old.ns;", await File.ReadAllTextAsync(newFile));
        Assert.NotNull(workspace.Context.GetDocumentByPath(newFile));
    }

    #endregion

    #region Helpers

    private static MoveTypeToNamespaceParams ValidParams(
        string? sourceFile = null,
        string symbolName = "Foo",
        string targetNamespace = "New.Ns") => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpMoveNsMissing.cs"),
            SymbolName = symbolName,
            TargetNamespace = targetNamespace
        };

    private static bool IsCaseSensitiveDirectory(string directory)
    {
        var probe = Path.Combine(directory, "RoslynMcpCaseProbe");
        Directory.CreateDirectory(probe);
        try
        {
            return !Directory.Exists(Path.Combine(directory, "roslynmcpcaseprobe"));
        }
        finally
        {
            try
            {
                Directory.Delete(probe);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";
        public string SecondaryProjectPath { get; init; } = "";
        public string SolutionPath { get; init; } = "";

        public string GetPath(string relativeFile) =>
            Path.Combine(
                DirectoryPath,
                relativeFile.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithExplicitCompileItemsAsync(
            params (string FileName, string Source)[] files) =>
            CreateWithCompileIncludesAsync(files.Select(f => f.FileName).ToArray(), files);

        public static Task<TempWorkspace> CreateWithCompileIncludesAsync(
            IReadOnlyList<string> includes,
            params (string FileName, string Source)[] files)
        {
            var compileItems = string.Join(
                Environment.NewLine,
                includes.Select(include => $"    <Compile Include=\"{include}\" />"));
            var projectXml = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultItems>false</EnableDefaultItems>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                {compileItems}
                  </ItemGroup>
                </Project>
                """;
            return CreateAsync(projectXml, files);
        }

        public static async Task<TempWorkspace> CreateLinkedFileSolutionAsync(
            params (string FileName, string Source)[] ownerFiles)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveNsLink_" + Guid.NewGuid().ToString("N"));
            var ownerDir = Path.Combine(directory, "Owner");
            var linkerDir = Path.Combine(directory, "Linker");
            Directory.CreateDirectory(ownerDir);
            Directory.CreateDirectory(linkerDir);

            await File.WriteAllTextAsync(Path.Combine(ownerDir, "Owner.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            string? sourcePath = null;
            foreach (var (fileName, source) in ownerFiles)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(ownerDir, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(ownerDir, "Foo.cs");
            var linkedInclude = string.Join(
                ';',
                ownerFiles.Select(f => "../Owner/" + f.FileName.Replace('\\', '/')));
            await File.WriteAllTextAsync(Path.Combine(linkerDir, "Linker.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultItems>false</EnableDefaultItems>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="{{linkedInclude}}" />
                  </ItemGroup>
                </Project>
                """);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Owner", "Owner\Owner.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Linker", "Linker\Linker.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                var linkedCount = context.Solution.Projects
                    .SelectMany(p => p.Documents)
                    .Count(d =>
                        d.FilePath != null
                        && string.Equals(
                            PathResolver.NormalizePath(d.FilePath),
                            PathResolver.NormalizePath(sourcePath),
                            StringComparison.OrdinalIgnoreCase));
                if (linkedCount < 2)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Expected linked document in both projects, found {linkedCount}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = Path.Combine(ownerDir, "Owner.csproj"),
                    SecondaryProjectPath = Path.Combine(linkerDir, "Linker.csproj"),
                    SolutionPath = solutionPath,
                    SourcePath = sourcePath,
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

        public static Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files) =>
            CreateAsync("""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """, files);

        public static async Task<TempWorkspace> CreateAsync(
            string projectXml,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpMoveNs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, projectXml);

            string? sourcePath = null;
            string? secondary = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                if (sourcePath == null)
                    sourcePath = path;
                else
                    secondary ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Foo.cs");

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
                    ProjectPath = projectPath,
                    SourcePath = sourcePath,
                    SecondarySourcePath = secondary ?? "",
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

    #endregion
}

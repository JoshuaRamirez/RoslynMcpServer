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

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";

        public string GetPath(string relativeFile) =>
            Path.Combine(
                DirectoryPath,
                relativeFile.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithExplicitCompileItemsAsync(
            params (string FileName, string Source)[] files)
        {
            var compileItems = string.Join(
                Environment.NewLine,
                files.Select(f => $"    <Compile Include=\"{f.FileName}\" />"));
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

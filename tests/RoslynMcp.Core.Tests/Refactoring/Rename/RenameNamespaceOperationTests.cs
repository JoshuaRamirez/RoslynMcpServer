using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Operation-level tests for <see cref="RenameNamespaceOperation"/>.
/// </summary>
public class RenameNamespaceOperationTests
{
    #region Validation

    [Fact]
    public void Validate_MissingSourceFile_ThrowsMissingRequiredParam()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: "")));
        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativeSourceFile_ThrowsInvalidSourcePath()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: "Foo.cs")));
        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_ThrowsSourceFileNotFound()
    {
        var ex = Assert.Throws<RefactoringException>(
            () => RenameNamespaceOperation.Validate(ValidParams()));
        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidNamespaceName_ThrowsInvalidNamespace()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsInvalidName.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, newName: "123Bad")));
            Assert.Equal(ErrorCodes.InvalidNamespace, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_SameName_ThrowsSameLocation()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsSameName.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, newName: "OldNs")));
            Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_UpdateFoldersTrue_ThrowsFolderUpdateNotSupported()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsFolders.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, updateFolders: true)));
            Assert.Equal(ErrorCodes.FolderUpdateNotSupported, ex.ErrorCode);
            Assert.Equal("3136", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_InvalidLine_ThrowsInvalidLineNumber()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsInvalidLine.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, line: 0)));
            Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsValidNamespaceName_AcceptsDottedAndSimpleNames()
    {
        Assert.True(RenameNamespaceOperation.IsValidNamespaceName("OldNs"));
        Assert.True(RenameNamespaceOperation.IsValidNamespaceName("MyApp.Services"));
        Assert.False(RenameNamespaceOperation.IsValidNamespaceName("123Bad"));
        Assert.False(RenameNamespaceOperation.IsValidNamespaceName("Has Space"));
        Assert.False(RenameNamespaceOperation.IsValidNamespaceName(""));
    }

    [Fact]
    public void IsLastSegmentRename_DetectsSharedParent()
    {
        Assert.True(RenameNamespaceOperation.IsLastSegmentRename("OldNs", "NewNs"));
        Assert.True(RenameNamespaceOperation.IsLastSegmentRename("MyApp.Old", "MyApp.New"));
        Assert.False(RenameNamespaceOperation.IsLastSegmentRename("MyApp.Old", "Other.New"));
        Assert.False(RenameNamespaceOperation.IsLastSegmentRename("OldNs", "OldNs"));
    }

    [Fact]
    public void GetParentAndLastSegment_SplitDottedNames()
    {
        Assert.Equal("", RenameNamespaceOperation.GetParentName("OldNs"));
        Assert.Equal("OldNs", RenameNamespaceOperation.GetLastSegment("OldNs"));
        Assert.Equal("MyApp.Services", RenameNamespaceOperation.GetParentName("MyApp.Services.Core"));
        Assert.Equal("Core", RenameNamespaceOperation.GetLastSegment("MyApp.Services.Core"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task RenameNamespace_Simple_UpdatesDeclarationAndUsings()
    {
        const string source = """
            namespace OldNs;

            public class Foo
            {
                public int Value { get; set; }
            }
            """;
        const string consumer = """
            using OldNs;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
                public OldNs.Foo Qualified() => new OldNs.Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Consumer.cs", consumer));
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "OldNs",
            NewName = "NewNs"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var declaration = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("namespace NewNs;", declaration);
        Assert.DoesNotContain("namespace OldNs;", declaration);
        Assert.Contains("public class Foo", declaration);

        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("using NewNs;", usages);
        Assert.DoesNotContain("using OldNs;", usages);
        Assert.Contains("new Foo()", usages);
        Assert.Contains("NewNs.Foo", usages);
        Assert.DoesNotContain("OldNs.Foo", usages);

        Assert.Contains(workspace.SourcePath, result.Changes!.FilesModified);
        Assert.Contains(workspace.SecondarySourcePath, result.Changes.FilesModified);
        Assert.Equal("NewNs", result.Symbol!.FullyQualifiedName);
        Assert.True(result.ReferencesUpdated >= 0);
    }

    [SkippableFact]
    public async Task RenameNamespace_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace OldNs;

            public class Foo
            {
            }
            """;
        const string consumer = """
            using OldNs;

            namespace Other;

            public class Consumer
            {
                public Foo Create() => new Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Consumer.cs", consumer));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "OldNs",
            NewName = "NewNs",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);

        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.Contains("namespace OldNs;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("using OldNs;", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task RenameNamespace_SameName_ThrowsSameLocation()
    {
        const string source = """
            namespace OldNs;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "OldNs",
                NewName = "OldNs"
            }));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_MissingNamespace_ThrowsSymbolNotFound()
    {
        const string source = """
            namespace OldNs;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "DoesNotExist",
                NewName = "NewNs"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_NameConflict_ThrowsNameConflictScope()
    {
        const string source = """
            namespace OldNs;

            public class Foo
            {
            }
            """;
        const string existing = """
            namespace NewNs;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Existing.cs", existing));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalExisting = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "OldNs",
                NewName = "NewNs"
            }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Equal("3010", ex.ErrorCode);
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalExisting, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    #endregion

    #region Helpers

    private static RenameNamespaceParams ValidParams(
        string? sourceFile = null,
        string namespaceName = "OldNs",
        string newName = "NewNs",
        int? line = null,
        bool updateFolders = false) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsMissing.cs"),
            NamespaceName = namespaceName,
            NewName = newName,
            Line = line,
            UpdateFolders = updateFolders
        };

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }
        public string SecondarySourcePath { get; init; } = "";

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync((fileName, source));

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, projectXml);

            string? sourcePath = null;
            string? secondary = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
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

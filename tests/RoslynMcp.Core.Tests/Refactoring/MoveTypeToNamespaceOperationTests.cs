using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring;

/// <summary>
/// Operation-level tests for <see cref="MoveTypeToNamespaceOperation"/>,
/// including optional <c>line</c>, <c>column</c>, <c>updateFileLocation</c>,
/// <c>preview</c>, and <c>allFiles</c>.
/// </summary>
public class MoveTypeToNamespaceOperationTests
{
    #region Input Validation

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "Widget",
            TargetNamespace = "New.Ns"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetNamespace = "New.Ns",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetNamespace = "New.Ns",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
        Assert.Equal("column must be >= 1.", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySymbolName_WithColumnAndLine_ThrowsMissingRequiredParam(string symbolName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = symbolName,
                TargetNamespace = "New.Ns",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptySourceFile_WithColumnAndLine_ThrowsMissingRequiredParam(string sourceFile)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                SourceFile = sourceFile,
                SymbolName = "Widget",
                TargetNamespace = "New.Ns",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTargetNamespace_WithColumnAndLine_ThrowsMissingRequiredParam(string targetNamespace)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                SourceFile = AbsoluteTestPath(),
                SymbolName = "Widget",
                TargetNamespace = targetNamespace,
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

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

    [Fact]
    public void AllFiles_DefaultsToFalse()
    {
        var @params = new MoveTypeToNamespaceParams
        {
            SourceFile = AbsoluteTestPath(),
            SymbolName = "Widget",
            TargetNamespace = "New.Ns"
        };

        Assert.False(@params.AllFiles);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = false,
                SymbolName = "Widget",
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileSymbolName_DoesNotThrow()
    {
        MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns"
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutTargetNamespace_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                TargetNamespace = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("targetNamespace", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                SymbolName = "Widget",
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                Line = 8,
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                Column = 1,
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_InvalidTargetNamespace_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            MoveTypeToNamespaceOperation.Validate(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                TargetNamespace = "1Bad.Ns"
            }));

        Assert.Equal(ErrorCodes.InvalidNamespace, ex.ErrorCode);
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

    #region P0 optional column disambiguation

    private const string SameLineDualTopLevelWidgetSource =
        "namespace A { public class Widget { } /* a-widget */ } namespace B { public class Widget { } /* b-widget */ }\n";

    private const string SeparateLineDualTopLevelWidgetSource = """
        namespace A
        {
            public class Widget { } // a-widget
        }

        namespace B
        {
            public class Widget { } // b-widget
        }
        """;

    private const string SingleWidgetSource = """
        namespace TestApp;

        public class Widget
        {
        }
        """;

    private const string NestedWidgetSource = """
        namespace TestApp;

        public class Outer
        {
            public class Widget { } // nested-widget
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToNamespace_OmittedColumn_LinePicksStartLineType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(SeparateLineDualTopLevelWidgetSource, "a-widget"),
            TargetNamespace = "Moved.A"
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.A", source);
        Assert.Contains("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace B", source);
        Assert.DoesNotContain("namespace A\n", source);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_OmittedColumn_MultipleMatches_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                TargetNamespace = "Moved.A"
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_OmittedColumn_SingleMatch_IgnoresLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = 1,
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace New.Ns;", source);
        Assert.Contains("class Widget", source);
        Assert.DoesNotContain("namespace TestApp;", source);
    }

    [Fact]
    public void FindCoveringType_OmittedColumn_StartLineEqualityPicksFirstOnSharedLine()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        Assert.Equal(2, matches.Count);

        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var a = matches.First(t => NamespaceOf(t) == "A");
        var startLine = a.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Assert.Equal(line, startLine);

        var firstStartLine = matches.First(t =>
            t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 == line);
        Assert.Equal("A", NamespaceOf(firstStartLine));
    }

    [Fact]
    public void FindCoveringType_ColumnOnAIdentifier_PicksA()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [Fact]
    public void FindCoveringType_ColumnOnBIdentifier_PicksB()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, line, ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"));

        Assert.NotNull(found);
        Assert.Equal("B", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnOnAIdentifier_PicksA()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetNamespace = "Moved.A"
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.A", source);
        Assert.Contains("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace B", source);
        Assert.DoesNotContain("namespace A {", source);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnOnBIdentifier_PicksB()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
            TargetNamespace = "Moved.B"
        });

        Assert.True(result.Success);
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.B", source);
        Assert.Contains("a-widget", source);
        Assert.Contains("b-widget", source);
        Assert.Contains("namespace A", source);
        Assert.DoesNotContain("namespace B {", source);
    }

    [Fact]
    public void FindCoveringType_ColumnWithoutLine_IsNotInvokedForOmittedLinePath()
    {
        var root = Parse(SameLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var bColumn = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */");
        var line = FindLine(SameLineDualTopLevelWidgetSource, "b-widget");
        var coveringB = TypeSymbolResolver.FindCoveringType(matches, line, bColumn);

        Assert.NotNull(coveringB);
        Assert.Equal("B", NamespaceOf(coveringB));
        Assert.Equal(2, matches.Count);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnWithoutLine_KeepsOmittedLinePath_SymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* b-widget */"),
                TargetNamespace = "Moved.B"
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindCoveringType_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        var root = Parse(source);
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-type");
        Assert.NotEqual(startLine, identifierLine);

        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(
            matches, identifierLine, ColumnOf(source, "Widget /* split-type */"));

        Assert.NotNull(found);
        Assert.Equal("A", NamespaceOf(found));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace A
            {
                public class
                    Widget /* split-type */
                {
                }
            }

            namespace B
            {
                public class Widget { } /* other-widget */
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var identifierLine = FindLine(source, "split-type");

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget /* split-type */"),
            TargetNamespace = "Moved.A"
        });

        Assert.True(result.Success);
        var remaining = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.A", remaining);
        Assert.Contains("split-type", remaining);
        Assert.Contains("other-widget", remaining);
        Assert.Contains("namespace B", remaining);
    }

    [Fact]
    public void FindCoveringType_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = Parse(SeparateLineDualTopLevelWidgetSource);
        var matches = TopLevelNamed(root, "Widget");
        var found = TypeSymbolResolver.FindCoveringType(matches, line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnAndLineMiss_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SeparateLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = 1,
                Column = 1,
                TargetNamespace = "Moved.A"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Contains("line 1, column 1", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_ColumnAndLine_UnknownSymbolName_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Missing",
                Line = 1,
                Column = 1,
                TargetNamespace = "Moved.A"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal("2003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_NestedType_StaysUnmoveable()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(NestedWidgetSource, "nested-widget");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                Line = line,
                Column = ColumnOf(NestedWidgetSource, "Widget { } // nested-widget"),
                TargetNamespace = "Moved.Nested"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_Column_Preview_WritesNothing_AndDescribesMove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetNamespace = "Moved.A",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change =>
            change.Description.Contains("Moved.A", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "namespace A { class Widget { } } namespace B { class Widget { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var first = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Widget");
        var span = first.Identifier.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, startCol));
        Assert.True(TypeSymbolResolver.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, endCol));
        Assert.False(TypeSymbolResolver.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_SequentialColumn_ReusedWorkspace_ActsOnSecondSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineDualTopLevelWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineDualTopLevelWidgetSource, "a-widget");

        var first = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineDualTopLevelWidgetSource, "Widget { } /* a-widget */"),
            TargetNamespace = "Moved.A"
        });
        Assert.True(first.Success);

        var afterFirst = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.A", afterFirst);
        Assert.Contains("b-widget", afterFirst);

        var second = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            Line = FindLine(afterFirst, "b-widget"),
            Column = ColumnOf(afterFirst, "Widget { } /* b-widget */"),
            TargetNamespace = "Moved.B"
        });
        Assert.True(second.Success);

        var afterSecond = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Moved.A", afterSecond);
        Assert.Contains("namespace Moved.B", afterSecond);
        Assert.Contains("a-widget", afterSecond);
        Assert.Contains("b-widget", afterSecond);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_Preview_WithoutColumn_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetNamespace = "New.Ns",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region AllFiles

    private const string AlphaSource = """
        namespace Old.Ns;

        public class Alpha
        {
        }
        """;

    private const string BetaSource = """
        namespace Other.Ns;

        public class Beta
        {
        }
        """;

    private const string AlreadyInTargetSource = """
        namespace New.Ns;

        public class Already
        {
        }
        """;

    private const string MultiTypeSource = """
        namespace Old.Ns;

        public class MultiA
        {
        }

        public class MultiB
        {
        }
        """;

    private const string NestedAndOuterSource = """
        namespace Old.Ns;

        public class Outer
        {
            public class Nested { } // nested-widget
        }
        """;

    private const string CollisionAlphaSource = """
        namespace A;

        public class Shared
        {
        }
        """;

    private const string CollisionBetaSource = """
        namespace B;

        public class Shared
        {
        }
        """;

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesFalse_MovesOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Alpha.cs", AlphaSource),
            ("Beta.cs", BetaSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeBeta = await File.ReadAllTextAsync(workspace.GetPath("Beta.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.GetPath("Alpha.cs"),
            AllFiles = false,
            SymbolName = "Alpha",
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.DoesNotContain("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Equal(beforeBeta, await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_OmittedAllFiles_KeepsSingleSiteMove()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("namespace TestApp;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_MovesEligibleAndSkipsAlreadyThereNestedIneligible()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Alpha.cs", AlphaSource),
            ("Beta.cs", BetaSource),
            ("Already.cs", AlreadyInTargetSource),
            ("Multi.cs", MultiTypeSource),
            ("WithNested.cs", NestedAndOuterSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeAlready = await File.ReadAllTextAsync(workspace.GetPath("Already.cs"));
        var beforeMulti = await File.ReadAllTextAsync(workspace.GetPath("Multi.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Contains("class Alpha", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));
        Assert.Contains("class Beta", await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));

        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.GetPath("Already.cs")));
        Assert.Equal(beforeMulti, await File.ReadAllTextAsync(workspace.GetPath("Multi.cs")));
        Assert.Contains("class MultiA", await File.ReadAllTextAsync(workspace.GetPath("Multi.cs")));
        Assert.Contains("class MultiB", await File.ReadAllTextAsync(workspace.GetPath("Multi.cs")));

        var outer = await File.ReadAllTextAsync(workspace.GetPath("WithNested.cs"));
        Assert.Contains("namespace New.Ns;", outer);
        Assert.Contains("class Outer", outer);
        Assert.Contains("class Nested", outer);
        Assert.DoesNotContain("namespace Old.Ns;", outer);

        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.GetPath("Alpha.cs")));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.GetPath("Beta.cs")));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.GetPath("WithNested.cs")));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.GetPath("Already.cs")));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.GetPath("Multi.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_WithoutSourceFileSymbolName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Alpha.cs", AlphaSource),
            ("Beta.cs", BetaSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesFalse_WithoutRequired_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                AllFiles = false,
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.Contains("namespace TestApp;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_WithSymbolName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                SymbolName = "Widget",
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("symbolName", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("namespace TestApp;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                Line = 1,
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SingleWidgetSource);
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new MoveTypeToNamespaceParams
            {
                AllFiles = true,
                Column = 1,
                TargetNamespace = "New.Ns"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_PreviewAllFiles_AggregatesChangesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Alpha.cs", AlphaSource),
            ("Beta.cs", BetaSource),
            ("Already.cs", AlreadyInTargetSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeAlpha = await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs"));
        var beforeBeta = await File.ReadAllTextAsync(workspace.GetPath("Beta.cs"));
        var beforeAlready = await File.ReadAllTextAsync(workspace.GetPath("Already.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.GetPath("Alpha.cs")));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.GetPath("Beta.cs")));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.GetPath("Already.cs")));
        Assert.Equal(beforeAlpha, await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Equal(beforeBeta, await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.GetPath("Already.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_EveryTypeNoOp_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Already.cs", AlreadyInTargetSource),
            ("Multi.cs", MultiTypeSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeAlready = await File.ReadAllTextAsync(workspace.GetPath("Already.cs"));
        var beforeMulti = await File.ReadAllTextAsync(workspace.GetPath("Multi.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(workspace.GetPath("Already.cs")));
        Assert.Equal(beforeMulti, await File.ReadAllTextAsync(workspace.GetPath("Multi.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_NameCollision_SkipsLater()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("AShared.cs", CollisionAlphaSource),
            ("BShared.cs", CollisionBetaSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.GetPath("AShared.cs"));
        var beforeB = await File.ReadAllTextAsync(workspace.GetPath("BShared.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.GetPath("AShared.cs"));
        var afterB = await File.ReadAllTextAsync(workspace.GetPath("BShared.cs"));
        var aMoved = afterA.Contains("namespace New.Ns;");
        var bMoved = afterB.Contains("namespace New.Ns;");

        Assert.True(aMoved ^ bMoved);
        Assert.True(beforeA.Contains("class Shared") && beforeB.Contains("class Shared"));
        Assert.True(result.Changes!.FilesModified.Count <= 1);
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Alpha.cs", AlphaSource),
            ("Beta.cs", BetaSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var beforeBeta = await File.ReadAllTextAsync(workspace.GetPath("Beta.cs"));

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            SourceFile = workspace.GetPath("Alpha.cs"),
            TargetNamespace = "New.Ns"
        });

        Assert.True(result.Success);
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.GetPath("Alpha.cs")));
        Assert.Equal(beforeBeta, await File.ReadAllTextAsync(workspace.GetPath("Beta.cs")));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_UpdateFileLocation_MovesEligibleFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Old/Ns/Alpha.cs", AlphaSource),
            ("Other/Ns/Beta.cs", BetaSource),
            ("New/Ns/Already.cs", AlreadyInTargetSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var oldAlpha = workspace.GetPath("Old/Ns/Alpha.cs");
        var oldBeta = workspace.GetPath("Other/Ns/Beta.cs");
        var newAlpha = workspace.GetPath("New/Ns/Alpha.cs");
        var newBeta = workspace.GetPath("New/Ns/Beta.cs");
        var already = workspace.GetPath("New/Ns/Already.cs");
        var beforeAlready = await File.ReadAllTextAsync(already);

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        Assert.False(File.Exists(oldAlpha));
        Assert.False(File.Exists(oldBeta));
        Assert.True(File.Exists(newAlpha));
        Assert.True(File.Exists(newBeta));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(newAlpha));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(newBeta));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(already));
        Assert.Contains(result.Changes!.FilesCreated, p => PathEquals(p, newAlpha));
        Assert.Contains(result.Changes.FilesCreated, p => PathEquals(p, newBeta));
        Assert.Contains(result.Changes.FilesDeleted, p => PathEquals(p, oldAlpha));
        Assert.Contains(result.Changes.FilesDeleted, p => PathEquals(p, oldBeta));
    }

    [SkippableFact]
    public async Task MoveTypeToNamespace_AllFilesTrue_UpdateFileLocation_DestinationCollision_SkipsLaterClaim()
    {
        const string gammaSource = """
            namespace A;

            public class Gamma
            {
            }
            """;
        const string deltaSource = """
            namespace B;

            public class Delta
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Extra/Shared.cs", gammaSource),
            ("Misc/Shared.cs", deltaSource));
        var operation = new MoveTypeToNamespaceOperation(workspace.Context);
        var dest = workspace.GetPath("New/Ns/Shared.cs");

        var result = await operation.ExecuteAsync(new MoveTypeToNamespaceParams
        {
            AllFiles = true,
            TargetNamespace = "New.Ns",
            UpdateFileLocation = true
        });

        Assert.True(result.Success);
        var aPath = workspace.GetPath("Extra/Shared.cs");
        var bPath = workspace.GetPath("Misc/Shared.cs");
        var aStillThere = File.Exists(aPath);
        var bStillThere = File.Exists(bPath);

        Assert.True(File.Exists(dest));
        Assert.True(aStillThere ^ bStillThere);
        Assert.True(result.Changes!.FilesCreated.Count(p => PathEquals(p, dest)) <= 1);

        var destText = await File.ReadAllTextAsync(dest);
        Assert.Contains("namespace New.Ns;", destText);
        if (aStillThere)
        {
            Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(aPath));
            Assert.Contains("class Gamma", await File.ReadAllTextAsync(aPath));
        }

        if (bStillThere)
        {
            Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(bPath));
            Assert.Contains("class Delta", await File.ReadAllTextAsync(bPath));
        }
    }

    [Fact]
    public void BuildAllFilesDescription_IncludesTypeAndNamespace()
    {
        Assert.Equal(
            "Change namespace of Widget to New.Ns",
            MoveTypeToNamespaceOperation.BuildAllFilesDescription("Widget", "New.Ns"));
    }

    [Fact]
    public void CollectTopLevelTypes_SkipsNested()
    {
        var types = MoveTypeToNamespaceOperation.CollectTopLevelTypes(Parse(NestedAndOuterSource));
        Assert.Single(types);
        Assert.Equal("Outer", types[0].Identifier.Text);
    }

    [Fact]
    public void GetNamespaceName_JoinsNestedNamespaceDeclarations()
    {
        var root = Parse("""
            namespace A
            {
                namespace B
                {
                    public class C { }
                }
            }
            """);
        var type = MoveTypeToNamespaceOperation.CollectTopLevelTypes(root).Single();
        Assert.Equal("A.B", MoveTypeToNamespaceOperation.GetNamespaceName(type));
    }

    #endregion

    #region Helpers

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath(string name = "Missing.cs") =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpMoveTypeToNamespace_" + name);

    private static SyntaxNode Parse(string source) =>
        CSharpSyntaxTree.ParseText(NormalizeNewlines(source)).GetRoot();

    private static List<TypeDeclarationSyntax> TopLevelNamed(SyntaxNode root, string name) =>
        root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static string NamespaceOf(TypeDeclarationSyntax type) =>
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();

    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

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

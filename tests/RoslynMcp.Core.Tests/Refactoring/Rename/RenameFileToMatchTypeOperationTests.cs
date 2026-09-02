using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Rename;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Rename;

/// <summary>
/// Operation-level tests for <see cref="RenameFileToMatchTypeOperation"/>.
/// </summary>
public class RenameFileToMatchTypeOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(ValidParams(sourceFile: "")));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(ValidParams(sourceFile: "Foo.cs")));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(ValidParams()));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(new RenameFileToMatchTypeParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFile_DoesNotThrow()
    {
        RenameFileToMatchTypeOperation.Validate(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                TypeName = "Bar"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.Validate(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameFileInvalidLine.cs");
        File.WriteAllText(path, "class C {}");
        try
        {
            var ex = Assert.Throws<RefactoringException>(() =>
                RenameFileToMatchTypeOperation.Validate(ValidParams(sourceFile: path, line: 0)));

            Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileNameMatchesType_ComparesNameWithoutExtension()
    {
        Assert.True(RenameFileToMatchTypeOperation.FileNameMatchesType("/tmp/Bar.cs", "Bar"));
        Assert.False(RenameFileToMatchTypeOperation.FileNameMatchesType("/tmp/Foo.cs", "Bar"));
    }

    [Fact]
    public void GetTargetFilePath_PreservesDirectoryAndExtension()
    {
        var source = Path.Combine(Path.DirectorySeparatorChar == '/' ? "/tmp/src" : @"C:\tmp\src", "Foo.cs");
        var target = RenameFileToMatchTypeOperation.GetTargetFilePath(source, "Bar");

        Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(target));
        Assert.Equal("Bar.cs", Path.GetFileName(target));
    }

    [Fact]
    public void ResolvePrimaryType_NoTypes_ThrowsTypeNotFound()
    {
        var types = Array.Empty<RenameFileToMatchTypeOperation.TypeTarget>();

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.ResolvePrimaryType(types, ValidParams()));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ResolvePrimaryType_MultipleTypesWithoutDisambiguation_ThrowsSymbolAmbiguous()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Alpha {}
            class Beta {}
            """);
        var types = RenameFileToMatchTypeOperation.FindTopLevelTypes(tree.GetRoot());

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameFileToMatchTypeOperation.ResolvePrimaryType(types, ValidParams()));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal(2, types.Count);
    }

    [Fact]
    public void ResolvePrimaryType_TypeNameDisambiguates()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Alpha {}
            class Beta {}
            """);
        var types = RenameFileToMatchTypeOperation.FindTopLevelTypes(tree.GetRoot());

        var selected = RenameFileToMatchTypeOperation.ResolvePrimaryType(
            types,
            ValidParams(typeName: "Beta"));

        Assert.Equal("Beta", selected.Name);
    }

    [Fact]
    public void FindTopLevelTypes_IgnoresNestedTypes()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Outer
            {
                class Inner {}
            }
            """);
        var types = RenameFileToMatchTypeOperation.FindTopLevelTypes(tree.GetRoot());

        Assert.Single(types);
        Assert.Equal("Outer", types[0].Name);
    }

    [Fact]
    public void FindTopLevelTypes_IncludesEnumAndDelegate()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            enum Status {}
            delegate void Callback();
            """);
        var types = RenameFileToMatchTypeOperation.FindTopLevelTypes(tree.GetRoot());

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "Status" && t.Node is EnumDeclarationSyntax);
        Assert.Contains(types, t => t.Name == "Callback" && t.Node is DelegateDeclarationSyntax);
    }

    [Fact]
    public void PathsDifferOnlyByCase_DetectsCaseOnlyPairs()
    {
        var dir = Path.DirectorySeparatorChar == '/' ? "/tmp/src" : @"C:\tmp\src";
        Assert.True(RenameFileToMatchTypeOperation.PathsDifferOnlyByCase(
            Path.Combine(dir, "foo.cs"),
            Path.Combine(dir, "Foo.cs")));
        Assert.False(RenameFileToMatchTypeOperation.PathsDifferOnlyByCase(
            Path.Combine(dir, "foo.cs"),
            Path.Combine(dir, "Bar.cs")));
        Assert.False(RenameFileToMatchTypeOperation.PathsDifferOnlyByCase(
            Path.Combine(dir, "foo.cs"),
            Path.Combine(dir, "foo.cs")));
    }

    [Fact]
    public void IsDestinationOccupiedByDifferentFile_CaseOnlySingleFile_IsNotOccupied()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameCaseOcc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "foo.cs");
            File.WriteAllText(source, "class Foo {}");
            var dest = Path.Combine(directory, "Foo.cs");

            // Old Windows-only ignore-case + ordinal-on-macOS check treated this as a
            // conflict whenever File.Exists(dest) was true. Must not reject.
            Assert.False(RenameFileToMatchTypeOperation.IsDestinationOccupiedByDifferentFile(source, dest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MoveSourceFile_CaseOnlyRename_ResultsInExactCasing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameCaseMove_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "foo.cs");
            File.WriteAllText(source, "class Foo {}");
            var dest = Path.Combine(directory, "Foo.cs");

            RenameFileToMatchTypeOperation.MoveSourceFile(source, dest);

            var names = Directory.GetFiles(directory).Select(Path.GetFileName).ToList();
            Assert.Single(names);
            Assert.Equal("Foo.cs", names[0]);
            Assert.Equal("class Foo {}", File.ReadAllText(dest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpdateExplicitCompileItems_RewritesIncludeAndLeavesOthers()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Foo.cs" />
                <Compile Include="Other.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = RenameFileToMatchTypeOperation.UpdateExplicitCompileItems(
            xml,
            projectDir,
            Path.Combine(projectDir, "Foo.cs"),
            Path.Combine(projectDir, "Bar.cs"));

        Assert.Contains("Include=\"Bar.cs\"", updated);
        Assert.DoesNotContain("Include=\"Foo.cs\"", updated);
        Assert.Contains("Include=\"Other.cs\"", updated);
    }

    [Fact]
    public void UpdateExplicitCompileItems_RewritesUpdateAndNamespacedInclude()
    {
        const string xml = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Compile Include="src\Foo.cs" />
                <Compile Update="src\Foo.cs">
                  <DependentUpon>Foo.tt</DependentUpon>
                </Compile>
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = RenameFileToMatchTypeOperation.UpdateExplicitCompileItems(
            xml,
            projectDir,
            Path.Combine(projectDir, "src", "Foo.cs"),
            Path.Combine(projectDir, "src", "Bar.cs"));

        Assert.Contains("Bar.cs", updated);
        Assert.DoesNotContain("Foo.cs", updated);
        Assert.Contains("Foo.tt", updated);
    }

    [Fact]
    public void UpdateExplicitCompileItems_SdkGlobProject_Unchanged()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = RenameFileToMatchTypeOperation.UpdateExplicitCompileItems(
            xml,
            projectDir,
            Path.Combine(projectDir, "Foo.cs"),
            Path.Combine(projectDir, "Bar.cs"));

        Assert.Equal(xml.ReplaceLineEndings(), updated.ReplaceLineEndings());
    }

    [Fact]
    public void RewriteProjectItemPath_PreservesDirectoryAndSeparator()
    {
        Assert.Equal("Bar.cs", RenameFileToMatchTypeOperation.RewriteProjectItemPath("Foo.cs", "Bar.cs"));
        Assert.Equal("src\\Bar.cs", RenameFileToMatchTypeOperation.RewriteProjectItemPath("src\\Foo.cs", "Bar.cs"));
        Assert.Equal("./Bar.cs", RenameFileToMatchTypeOperation.RewriteProjectItemPath("./Foo.cs", "Bar.cs"));
    }

    #endregion

    #region Happy Path

    [SkippableFact]
    public async Task RenameFile_Simple_RenamesFileAndPreservesTypeText()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
                public Bar() { }
                public int Value { get; set; }
            }
            """;
        const string consumer = """
            namespace TestApp;

            public class Consumer
            {
                public Bar Create() => new Bar();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Consumer.cs", consumer));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var newPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.True(File.Exists(newPath));

        var moved = await File.ReadAllTextAsync(newPath);
        Assert.Equal(source.ReplaceLineEndings(), moved.ReplaceLineEndings());
        Assert.Contains("public class Bar", moved);
        Assert.Contains("public Bar()", moved);

        var consumerAfter = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Equal(originalConsumer, consumerAfter);
        Assert.Contains("new Bar()", consumerAfter);

        Assert.NotNull(workspace.Context.GetDocumentByPath(newPath));
        Assert.Null(workspace.Context.GetDocumentByPath(workspace.SourcePath));
        Assert.Contains(newPath, result.Changes!.FilesCreated);
        Assert.Contains(workspace.SourcePath, result.Changes.FilesDeleted);
        Assert.Equal("Bar", result.Symbol!.Name);
        Assert.Equal(0, result.ReferencesUpdated);
    }

    [SkippableFact]
    public async Task RenameFile_Preview_ReturnsChangesAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);

        var newPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(newPath));
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(workspace.Context.GetDocumentByPath(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_CaseOnly_RenamesOnDiskCasing()
    {
        const string source = """
            namespace TestApp;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);

        var newPath = Path.Combine(workspace.DirectoryPath, "Foo.cs");
        var names = Directory.GetFiles(workspace.DirectoryPath, "*.cs").Select(Path.GetFileName).ToList();
        Assert.Contains("Foo.cs", names);
        Assert.DoesNotContain("foo.cs", names);
        Assert.True(File.Exists(newPath));
        Assert.Contains("public class Foo", await File.ReadAllTextAsync(newPath));
        Assert.NotNull(workspace.Context.GetDocumentByPath(newPath));
    }

    [SkippableFact]
    public async Task RenameFile_ExplicitCompileItem_UpdatesProjectFile()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;
        const string other = """
            namespace TestApp;

            public class Other
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Foo.cs", source),
            ("Other.cs", other));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);

        var newPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        Assert.True(File.Exists(newPath));
        Assert.False(File.Exists(workspace.SourcePath));

        var csproj = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.Contains("Include=\"Bar.cs\"", csproj);
        Assert.DoesNotContain("Include=\"Foo.cs\"", csproj);
        Assert.Contains("Include=\"Other.cs\"", csproj);
        Assert.Contains(workspace.ProjectPath, result.Changes!.FilesModified);

        workspace.Context.Dispose();
        var provider = new MSBuildWorkspaceProvider();
        using var reloaded = await provider.CreateContextAsync(workspace.ProjectPath);
        Assert.NotNull(reloaded.GetDocumentByPath(newPath));
        Assert.Null(reloaded.GetDocumentByPath(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_ExplicitCompileItem_Preview_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Foo.cs", source));
        var originalProject = await File.ReadAllTextAsync(workspace.ProjectPath);
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains(result.PendingChanges!, c => c.File == workspace.ProjectPath);

        Assert.Equal(originalProject, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Bar.cs")));
        Assert.Contains("Include=\"Foo.cs\"", originalProject);
    }

    [SkippableFact]
    public async Task RenameFile_TypeName_DisambiguatesMultipleTypes()
    {
        const string source = """
            namespace TestApp;

            public class Alpha { }
            public class Beta { }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta"
        });

        Assert.True(result.Success);

        var newPath = Path.Combine(workspace.DirectoryPath, "Beta.cs");
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.True(File.Exists(newPath));
        Assert.Equal(original.ReplaceLineEndings(), (await File.ReadAllTextAsync(newPath)).ReplaceLineEndings());
        Assert.Contains("public class Alpha", await File.ReadAllTextAsync(newPath));
    }

    #endregion

    #region Rejects

    [SkippableFact]
    public async Task RenameFile_AlreadyMatches_ThrowsSameLocation()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Bar.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.SameLocation, ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_DestinationExists_ThrowsTargetFileExists()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;
        const string existing = """
            namespace TestApp;

            public class Other
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Bar.cs", existing));
        var fooOriginal = await File.ReadAllTextAsync(workspace.SourcePath);
        var barOriginal = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.True(File.Exists(workspace.SecondarySourcePath));
        Assert.Equal(fooOriginal, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(barOriginal, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_MultipleTypesWithoutDisambiguation_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace TestApp;

            public class Alpha { }
            public class Beta { }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Beta.cs")));
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_NoTypeInFile_ThrowsTypeNotFound()
    {
        const string source = """
            namespace TestApp;

            // no types
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
    }

    #endregion

    #region AllFiles

    [SkippableFact]
    public async Task RenameFile_AllFilesFalse_RenamesOnlySpecifiedFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"),
            ("Bar.cs", "namespace TestApp; public class Beta { }"),
            ("Already.cs", "namespace TestApp; public class Already { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var alreadyPath = Path.Combine(workspace.DirectoryPath, "Already.cs");
        var beforeBar = await File.ReadAllTextAsync(barPath);
        var beforeAlready = await File.ReadAllTextAsync(alreadyPath);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath,
            AllFiles = false
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.True(File.Exists(barPath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Beta.cs")));
        Assert.Equal(beforeBar, await File.ReadAllTextAsync(barPath));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(alreadyPath));
        Assert.Contains(Path.Combine(workspace.DirectoryPath, "Alpha.cs"), result.Changes!.FilesCreated);
        Assert.DoesNotContain(result.Changes.FilesCreated, p => Path.GetFileName(p) == "Beta.cs");
    }

    [SkippableFact]
    public async Task RenameFile_OmittedAllFiles_KeepsSingleFileRename()
    {
        const string source = """
            namespace TestApp;

            public class Bar
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Bar.cs")));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_RenamesUnambiguousMismatchedAndSkipsOthers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"),
            ("Qux.cs", "namespace TestApp; public class Beta { }"),
            ("Already.cs", "namespace TestApp; public class Already { }"),
            ("Multi.cs", "namespace TestApp; public class One { } public class Two { }"),
            ("Empty.cs", "namespace TestApp;\n"),
            ("OccupiedSrc.cs", "namespace TestApp; public class OccupiedDest { }"),
            ("OccupiedDest.cs", "namespace TestApp; public class OccupiedDest { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var alreadyPath = Path.Combine(workspace.DirectoryPath, "Already.cs");
        var multiPath = Path.Combine(workspace.DirectoryPath, "Multi.cs");
        var emptyPath = Path.Combine(workspace.DirectoryPath, "Empty.cs");
        var occupiedSrc = Path.Combine(workspace.DirectoryPath, "OccupiedSrc.cs");
        var occupiedDest = Path.Combine(workspace.DirectoryPath, "OccupiedDest.cs");
        var beforeAlready = await File.ReadAllTextAsync(alreadyPath);
        var beforeMulti = await File.ReadAllTextAsync(multiPath);
        var beforeEmpty = await File.ReadAllTextAsync(emptyPath);
        var beforeOccupiedSrc = await File.ReadAllTextAsync(occupiedSrc);
        var beforeOccupiedDest = await File.ReadAllTextAsync(occupiedDest);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);

        var alphaPath = Path.Combine(workspace.DirectoryPath, "Alpha.cs");
        var betaPath = Path.Combine(workspace.DirectoryPath, "Beta.cs");
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Qux.cs")));
        Assert.True(File.Exists(alphaPath));
        Assert.True(File.Exists(betaPath));
        Assert.Contains("public class Alpha", await File.ReadAllTextAsync(alphaPath));
        Assert.Contains("public class Beta", await File.ReadAllTextAsync(betaPath));

        Assert.True(File.Exists(alreadyPath));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(alreadyPath));
        Assert.True(File.Exists(multiPath));
        Assert.Equal(beforeMulti, await File.ReadAllTextAsync(multiPath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "One.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Two.cs")));
        Assert.True(File.Exists(emptyPath));
        Assert.Equal(beforeEmpty, await File.ReadAllTextAsync(emptyPath));
        Assert.True(File.Exists(occupiedSrc));
        Assert.True(File.Exists(occupiedDest));
        Assert.Equal(beforeOccupiedSrc, await File.ReadAllTextAsync(occupiedSrc));
        Assert.Equal(beforeOccupiedDest, await File.ReadAllTextAsync(occupiedDest));

        Assert.Equal(2, result.Changes!.FilesCreated.Count);
        Assert.Contains(alphaPath, result.Changes.FilesCreated);
        Assert.Contains(betaPath, result.Changes.FilesCreated);
        Assert.Contains(workspace.SourcePath, result.Changes.FilesDeleted);
        Assert.Contains(Path.Combine(workspace.DirectoryPath, "Qux.cs"), result.Changes.FilesDeleted);
        Assert.DoesNotContain(result.Changes.FilesCreated, p => Path.GetFileName(p) == "OccupiedDest.cs");
        Assert.DoesNotContain(alreadyPath, result.Changes.FilesDeleted);
        Assert.DoesNotContain(multiPath, result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_WithoutSourceFile_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"),
            ("Qux.cs", "namespace TestApp; public class Beta { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesCreated.Count);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Beta.cs")));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace TestApp; public class Bar { }",
            "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                AllFiles = false
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace TestApp; public class Bar { }",
            "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                TypeName = "Bar"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Bar.cs")));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace TestApp; public class Bar { }",
            "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace TestApp; public class Bar { }",
            "Foo.cs");
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameFileToMatchTypeParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RenameFile_PreviewAllFiles_AggregatesChangesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"),
            ("Qux.cs", "namespace TestApp; public class Beta { }"),
            ("Already.cs", "namespace TestApp; public class Already { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var fooPath = workspace.SourcePath;
        var quxPath = Path.Combine(workspace.DirectoryPath, "Qux.cs");
        var alreadyPath = Path.Combine(workspace.DirectoryPath, "Already.cs");
        var beforeFoo = await File.ReadAllTextAsync(fooPath);
        var beforeQux = await File.ReadAllTextAsync(quxPath);
        var beforeAlready = await File.ReadAllTextAsync(alreadyPath);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c => c.File == fooPath);
        Assert.Contains(result.PendingChanges, c => c.File == Path.Combine(workspace.DirectoryPath, "Alpha.cs"));
        Assert.Contains(result.PendingChanges, c => c.File == quxPath);
        Assert.Contains(result.PendingChanges, c => c.File == Path.Combine(workspace.DirectoryPath, "Beta.cs"));
        Assert.DoesNotContain(result.PendingChanges, c => c.File == alreadyPath);
        Assert.True(File.Exists(fooPath));
        Assert.True(File.Exists(quxPath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Beta.cs")));
        Assert.Equal(beforeFoo, await File.ReadAllTextAsync(fooPath));
        Assert.Equal(beforeQux, await File.ReadAllTextAsync(quxPath));
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(alreadyPath));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_EveryFileNoOp_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Already.cs", "namespace TestApp; public class Already { }"),
            ("Multi.cs", "namespace TestApp; public class One { } public class Two { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var alreadyPath = workspace.SourcePath;
        var multiPath = Path.Combine(workspace.DirectoryPath, "Multi.cs");
        var beforeAlready = await File.ReadAllTextAsync(alreadyPath);
        var beforeMulti = await File.ReadAllTextAsync(multiPath);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.Empty(result.Changes.FilesDeleted);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeAlready, await File.ReadAllTextAsync(alreadyPath));
        Assert.Equal(beforeMulti, await File.ReadAllTextAsync(multiPath));
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_DestinationCollision_SkipsBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", "namespace TestApp; public class Shared { }"),
            ("Bar.cs", "namespace TestApp; public class Shared { }"),
            ("Keep.cs", "namespace TestApp; public class Alpha { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);
        var fooPath = workspace.SourcePath;
        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var keepPath = Path.Combine(workspace.DirectoryPath, "Keep.cs");
        var beforeFoo = await File.ReadAllTextAsync(fooPath);
        var beforeBar = await File.ReadAllTextAsync(barPath);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(fooPath));
        Assert.True(File.Exists(barPath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Shared.cs")));
        Assert.Equal(beforeFoo, await File.ReadAllTextAsync(fooPath));
        Assert.Equal(beforeBar, await File.ReadAllTextAsync(barPath));
        Assert.False(File.Exists(keepPath));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.DoesNotContain(result.Changes!.FilesCreated, p => Path.GetFileName(p) == "Shared.cs");
        Assert.Contains(Path.Combine(workspace.DirectoryPath, "Alpha.cs"), result.Changes.FilesCreated);
    }

    [SkippableFact]
    public async Task RenameFile_AllFilesTrue_ExplicitCompileItems_UpdatesProjectOnce()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"),
            ("Qux.cs", "namespace TestApp; public class Beta { }"));
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "Beta.cs")));
        Assert.False(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Qux.cs")));

        var csproj = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.Contains("Include=\"Alpha.cs\"", csproj);
        Assert.Contains("Include=\"Beta.cs\"", csproj);
        Assert.DoesNotContain("Include=\"Foo.cs\"", csproj);
        Assert.DoesNotContain("Include=\"Qux.cs\"", csproj);
        Assert.Contains(workspace.ProjectPath, result.Changes!.FilesModified);
        Assert.Single(result.Changes.FilesModified);
    }

    [SkippableFact]
    public async Task RenameFile_PreviewAllFiles_ExplicitCompileItems_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithExplicitCompileItemsAsync(
            ("Foo.cs", "namespace TestApp; public class Alpha { }"));
        var originalProject = await File.ReadAllTextAsync(workspace.ProjectPath);
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameFileToMatchTypeOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameFileToMatchTypeParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains(result.PendingChanges!, c => c.File == workspace.ProjectPath);
        Assert.Equal(originalProject, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "Alpha.cs")));
    }

    #endregion

    #region Helpers

    private static RenameFileToMatchTypeParams ValidParams(
        string? sourceFile = null,
        string? typeName = null,
        int? line = null) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpRenameFileMissing.cs"),
            TypeName = typeName,
            Line = line
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameFile_" + Guid.NewGuid().ToString("N"));
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

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

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameFile_" + Guid.NewGuid().ToString("N"));
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

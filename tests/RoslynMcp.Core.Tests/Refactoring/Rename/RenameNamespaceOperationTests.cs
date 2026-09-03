using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
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
    public void Validate_UpdateFoldersTrue_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsFolders.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, updateFolders: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryFindMatchingFolder_TrailingSegmentsEqualNamespaceParts()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/repo" : @"C:\repo";
        var file = Path.Combine(root, "src", "Old", "Ns", "Foo.cs");

        Assert.Equal(
            Path.Combine(root, "src", "Old", "Ns"),
            RenameNamespaceOperation.TryFindMatchingFolder(file, "Old.Ns"));
        Assert.Equal(
            Path.Combine(root, "src", "Old"),
            RenameNamespaceOperation.TryFindMatchingFolder(file, "Old"));
        Assert.Null(RenameNamespaceOperation.TryFindMatchingFolder(Path.Combine(root, "src", "Foo.cs"), "Old.Ns"));
    }

    [Fact]
    public void GetDestinationFolder_ReplacesTrailingNamespaceSegments()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/repo" : @"C:\repo";
        var source = Path.Combine(root, "src", "Old", "Ns");

        Assert.Equal(
            Path.Combine(root, "src", "New", "Ns"),
            RenameNamespaceOperation.GetDestinationFolder(source, "Old.Ns", "New.Ns"));
        Assert.Equal(
            Path.Combine(root, "src", "New"),
            RenameNamespaceOperation.GetDestinationFolder(Path.Combine(root, "src", "Old"), "Old", "New"));
    }

    [Fact]
    public void RemapPathUnderFolder_PreservesRelativeTail()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/repo" : @"C:\repo";
        var source = Path.Combine(root, "src", "Old", "Ns");
        var dest = Path.Combine(root, "src", "New", "Ns");
        var file = Path.Combine(source, "Sub", "Foo.cs");

        Assert.Equal(
            Path.Combine(dest, "Sub", "Foo.cs"),
            RenameNamespaceOperation.RemapPathUnderFolder(file, source, dest));
    }

    [Fact]
    public void TryFindMatchingFolder_StopAtProjectDirectory_ExcludesProjectRoot()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/repo" : @"C:\repo";
        var projectDir = Path.Combine(root, "Old", "Ns");
        var file = Path.Combine(projectDir, "Foo.cs");

        Assert.Equal(projectDir, RenameNamespaceOperation.TryFindMatchingFolder(file, "Old.Ns"));
        Assert.Null(RenameNamespaceOperation.TryFindMatchingFolder(file, "Old.Ns", projectDir));
    }

    [Fact]
    public void IsDestinationNestedInSource_WhenNewNameExtendsOldNamespace()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/repo" : @"C:\repo";
        var source = Path.Combine(root, "src", "Old", "Ns");
        var dest = RenameNamespaceOperation.GetDestinationFolder(source, "Old.Ns", "Old.Ns.Sub");

        Assert.Equal(Path.Combine(source, "Sub"), dest);
        Assert.True(RenameNamespaceOperation.IsDestinationNestedInSource(source, dest));
        Assert.False(RenameNamespaceOperation.IsDestinationNestedInSource(
            source,
            Path.Combine(root, "src", "New", "Ns")));
    }

    [Fact]
    public void GetGlobDirectoryPrefix_StopsBeforeWildcard()
    {
        Assert.Equal("Old/Ns", RenameNamespaceOperation.GetGlobDirectoryPrefix("Old/Ns/**/*.cs"));
        Assert.Equal("Old/Ns", RenameNamespaceOperation.GetGlobDirectoryPrefix("Old/Ns/*.cs"));
        Assert.Equal("", RenameNamespaceOperation.GetGlobDirectoryPrefix("**/*.cs"));
        Assert.True(RenameNamespaceOperation.ContainsMsBuildGlob("Old/Ns/**/*.cs"));
        Assert.False(RenameNamespaceOperation.ContainsMsBuildGlob("Old/Ns/Foo.cs"));
    }

    [Fact]
    public void UpdateExplicitCompileItemsForMoves_RewritesWildcardAndSemicolonList()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Old/Ns/**/*.cs" />
                <Compile Include="Old/Ns/*.cs;Consumer.cs" />
                <Compile Include="**/*.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var sourceFolder = Path.Combine(projectDir, "Old", "Ns");
        var destFolder = Path.Combine(projectDir, "New", "Ns");
        var updated = RenameNamespaceOperation.UpdateExplicitCompileItemsForMoves(
            xml,
            projectDir,
            [(Path.Combine(sourceFolder, "Foo.cs"), Path.Combine(destFolder, "Foo.cs"))],
            [(sourceFolder, destFolder)]);

        Assert.Contains("New/Ns/**/*.cs", updated);
        Assert.DoesNotContain("Old/Ns/**/*.cs", updated);
        Assert.Contains("New/Ns/*.cs;Consumer.cs", updated);
        Assert.Contains("**/*.cs", updated);
    }

    [Fact]
    public void UpdateExplicitCompileItemsForMoves_AncestorWildcard_ThrowsUnsupportedCompileItem()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Old/**/*.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var sourceFolder = Path.Combine(projectDir, "Old", "Ns");
        var destFolder = Path.Combine(projectDir, "New", "Ns");

        var ex = Assert.Throws<RefactoringException>(() =>
            RenameNamespaceOperation.UpdateExplicitCompileItemsForMoves(
                xml,
                projectDir,
                [(Path.Combine(sourceFolder, "Foo.cs"), Path.Combine(destFolder, "Foo.cs"))],
                [(sourceFolder, destFolder)]));

        Assert.Equal(ErrorCodes.UnsupportedCompileItem, ex.ErrorCode);
        Assert.Equal("3141", ex.ErrorCode);
    }

    [Fact]
    public void UpdateExplicitCompileItemsForMoves_RewritesDirectoryAndLeavesOthers()
    {
        const string xml = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="Old/Ns/Foo.cs" />
                <Compile Include="Consumer.cs" />
              </ItemGroup>
            </Project>
            """;
        var projectDir = Path.DirectorySeparatorChar == '/' ? "/tmp/proj" : @"C:\tmp\proj";
        var updated = RenameNamespaceOperation.UpdateExplicitCompileItemsForMoves(
            xml,
            projectDir,
            [(Path.Combine(projectDir, "Old", "Ns", "Foo.cs"), Path.Combine(projectDir, "New", "Ns", "Foo.cs"))]);

        Assert.Contains("New/Ns/Foo.cs", updated);
        Assert.DoesNotContain("Old/Ns/Foo.cs", updated);
        Assert.Contains("Consumer.cs", updated);
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
    public void Validate_InvalidColumn_ThrowsInvalidColumnNumber()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsInvalidColumn.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, column: 0)));
            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_NegativeColumn_ThrowsInvalidColumnNumber()
    {
        var path = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsNegativeColumn.cs");
        File.WriteAllText(path, "namespace OldNs;");
        try
        {
            var ex = Assert.Throws<RefactoringException>(
                () => RenameNamespaceOperation.Validate(ValidParams(sourceFile: path, column: -1)));
            Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
            Assert.Equal("1007", ex.ErrorCode);
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

    [Fact]
    public void GetRelativeNamespaceName_StripsEnclosingPrefix()
    {
        Assert.Equal("X.Y", RenameNamespaceOperation.GetRelativeNamespaceName("X.Y", ""));
        Assert.Equal("Y", RenameNamespaceOperation.GetRelativeNamespaceName("X.Y", "X"));
        Assert.Equal("C", RenameNamespaceOperation.GetRelativeNamespaceName("X.Y.C", "X.Y"));
        Assert.Equal("Y.C", RenameNamespaceOperation.GetRelativeNamespaceName("X.Y.C", "X"));
    }

    [Fact]
    public void GetCorrespondingPrefix_MapsAncestorOntoNewPath()
    {
        Assert.Equal("X", RenameNamespaceOperation.GetCorrespondingPrefix("A.B", "X.Y", "A"));
        Assert.Equal("X.Y", RenameNamespaceOperation.GetCorrespondingPrefix("A.B", "X.Y", "A.B"));
        Assert.Equal("X.Y.C", RenameNamespaceOperation.GetCorrespondingPrefix("A.B", "X.Y", "A.B.C"));
    }

    [Fact]
    public void HasNamespaceCollision_DescendantTypes_IsDetected()
    {
        var compilation = CreateCompilation("""
            namespace Old.Sub { public class Foo {} }
            namespace Dest.New.Sub { public class Foo {} }
            """);

        Assert.True(RenameNamespaceOperation.HasNamespaceCollision(
            FindCompilationNamespace(compilation, "Old"),
            FindCompilationNamespace(compilation, "Dest.New")));
    }

    [Fact]
    public void HasNamespaceCollision_NamespaceVersusType_IsDetected()
    {
        var compilation = CreateCompilation("""
            namespace Old { public class Sub {} }
            namespace Dest.New.Sub { public class Bar {} }
            """);

        Assert.True(RenameNamespaceOperation.HasNamespaceCollision(
            FindCompilationNamespace(compilation, "Old"),
            FindCompilationNamespace(compilation, "Dest.New")));
    }

    [Fact]
    public void HasNamespaceCollision_NoOverlap_ReturnsFalse()
    {
        var compilation = CreateCompilation("""
            namespace Old.Sub { public class Foo {} }
            namespace Dest.New.Sub { public class Bar {} }
            """);

        Assert.False(RenameNamespaceOperation.HasNamespaceCollision(
            FindCompilationNamespace(compilation, "Old"),
            FindCompilationNamespace(compilation, "Dest.New")));
    }

    [Fact]
    public void Rewriter_NestedDeclaration_WritesRelativeName()
    {
        var compilation = CreateCompilation("""
            namespace A
            {
                namespace B
                {
                    public class Foo {}
                }
            }
            """);
        var tree = compilation.SyntaxTrees.Single();
        var rewritten = RenameNamespaceOperation.RewriteNamespaceNames(
            compilation.GetSemanticModel(tree),
            tree.GetRoot(),
            FindCompilationNamespace(compilation, "A.B"),
            "A.B",
            "X.Y");
        var text = rewritten.ToFullString();

        Assert.Contains("namespace X", text);
        Assert.Contains("namespace Y", text);
        Assert.DoesNotContain("namespace X.Y", text);
        Assert.DoesNotContain("namespace A", text);
        Assert.DoesNotContain("namespace B", text);
    }

    [Fact]
    public void Rewriter_QualifiedTypeName_RewritesContainingNameWithoutThrowing()
    {
        var compilation = CreateCompilation("""
            namespace A.B { public class Foo {} }
            class C
            {
                A.B.Foo f = new A.B.Foo();
            }
            """);
        var tree = compilation.SyntaxTrees.Single();
        var rewritten = RenameNamespaceOperation.RewriteNamespaceNames(
            compilation.GetSemanticModel(tree),
            tree.GetRoot(),
            FindCompilationNamespace(compilation, "A.B"),
            "A.B",
            "X.Y");
        var text = rewritten.ToFullString();

        Assert.Contains("namespace X.Y", text);
        Assert.Contains("X.Y.Foo", text);
        Assert.DoesNotContain("A.B.Foo", text);
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

    [SkippableFact]
    public async Task RenameNamespace_NestedDeclaration_PreservesRelativeName()
    {
        const string source = """
            namespace A
            {
                namespace B
                {
                    public class Foo
                    {
                    }
                }
            }
            """;
        const string consumer = """
            namespace Other;

            public class Consumer
            {
                public A.B.Foo Create() => new A.B.Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Consumer.cs", consumer));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var preview = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "A.B",
            NewName = "X.Y",
            Preview = true
        });

        Assert.True(preview.Success);
        Assert.True(preview.Preview);
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "A.B",
            NewName = "X.Y"
        });

        Assert.True(result.Success);

        var declaration = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.DoesNotContain("namespace X.Y", declaration);
        Assert.Contains("namespace X", declaration);
        Assert.Contains("namespace Y", declaration);
        Assert.DoesNotContain("namespace A", declaration);
        Assert.DoesNotContain("namespace B", declaration);
        Assert.Contains("public class Foo", declaration);

        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("X.Y.Foo", usages);
        Assert.DoesNotContain("A.B.Foo", usages);
    }

    [SkippableFact]
    public async Task RenameNamespace_QualifiedName_DottedRename_DoesNotThrow()
    {
        const string source = """
            namespace A.B;

            public class Foo
            {
            }
            """;
        const string consumer = """
            namespace Other;

            public class Consumer
            {
                public A.B.Foo Create() => new A.B.Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", source),
            ("Consumer.cs", consumer));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var preview = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "A.B",
            NewName = "X.Y",
            Preview = true
        });

        Assert.True(preview.Success);
        Assert.True(preview.Preview);
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "A.B",
            NewName = "X.Y"
        });

        Assert.True(result.Success);
        Assert.Contains("namespace X.Y;", await File.ReadAllTextAsync(workspace.SourcePath));
        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("X.Y.Foo", usages);
        Assert.DoesNotContain("A.B.Foo", usages);
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

    [SkippableFact]
    public async Task RenameNamespace_DescendantCollision_ThrowsNameConflictScope()
    {
        const string source = """
            namespace Old.Sub;

            public class Foo
            {
            }
            """;
        const string existing = """
            namespace Dest.New.Sub;

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
                NamespaceName = "Old",
                NewName = "Dest.New"
            }));

        Assert.Equal(ErrorCodes.NameConflictScope, ex.ErrorCode);
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalExisting, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersFalse_LeavesFolders()
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
        var oldFolder = workspace.GetPath("Old/Ns");
        var newFolder = workspace.GetPath("New/Ns");
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = false
        });

        Assert.True(result.Success);
        Assert.True(Directory.Exists(oldFolder));
        Assert.True(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));
        Assert.False(Directory.Exists(newFolder));
        Assert.Contains("namespace New.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("using New.Ns;", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.DoesNotContain("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_MovesMatchingFolderAndUpdatesUsings()
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
                public Old.Ns.Foo Qualified() => new Old.Ns.Foo();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var oldFile = workspace.GetPath("Old/Ns/Foo.cs");
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = true
        });

        Assert.True(result.Success);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.False(Directory.Exists(workspace.GetPath("Old/Ns")));
        Assert.True(Directory.Exists(workspace.GetPath("New/Ns")));

        var declaration = await File.ReadAllTextAsync(newFile);
        Assert.Contains("namespace New.Ns;", declaration);
        Assert.DoesNotContain("namespace Old.Ns;", declaration);

        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("using New.Ns;", usages);
        Assert.DoesNotContain("using Old.Ns;", usages);
        Assert.Contains("New.Ns.Foo", usages);
        Assert.DoesNotContain("Old.Ns.Foo", usages);

        Assert.NotNull(workspace.Context.GetDocumentByPath(newFile));
        Assert.Null(workspace.Context.GetDocumentByPath(oldFile));
        Assert.Contains(newFile, result.Changes!.FilesCreated);
        Assert.Contains(oldFile, result.Changes.FilesDeleted);
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_Preview_WritesNothing()
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
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.ChangeType == ChangeKind.Create &&
            string.Equals(
                Path.GetFullPath(c.File),
                Path.GetFullPath(workspace.GetPath("New/Ns")),
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
    public async Task RenameNamespace_UpdateFoldersTrue_DestinationExists_ThrowsDestinationFolderExists()
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
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "Old.Ns",
                NewName = "New.Ns",
                UpdateFolders = true
            }));

        Assert.Equal(ErrorCodes.DestinationFolderExists, ex.ErrorCode);
        Assert.Equal("3137", ex.ErrorCode);
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.True(Directory.Exists(workspace.GetPath("Old/Ns")));
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_FolderDoesNotMatch_Throws()
    {
        const string source = """
            namespace Old.Ns;

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
                NamespaceName = "Old.Ns",
                NewName = "New.Ns",
                UpdateFolders = true
            }));

        Assert.Equal(ErrorCodes.FolderDoesNotMatchNamespace, ex.ErrorCode);
        Assert.Equal("3138", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(Directory.Exists(workspace.GetPath("New/Ns")));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_ExplicitCompileItem_UpdatesProjectFile()
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
        var operation = new RenameNamespaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = true
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
    public async Task RenameNamespace_UpdateFoldersTrue_NestedDestination_ThrowsAndWritesNothing()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Old/Ns/Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "Old.Ns",
                NewName = "Old.Ns.Sub",
                UpdateFolders = true
            }));

        Assert.Equal(ErrorCodes.DestinationNestedInSource, ex.ErrorCode);
        Assert.Equal("3139", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.True(Directory.Exists(workspace.GetPath("Old/Ns")));
        Assert.False(Directory.Exists(workspace.GetPath("Old/Ns/Sub")));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_ProjectRoot_ThrowsAndLeavesProjectFile()
    {
        const string source = """
            namespace Old.Ns;

            public class Foo
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithProjectInFolderAsync("Old/Ns", ("Old/Ns/Foo.cs", source));
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalProject = await File.ReadAllTextAsync(workspace.ProjectPath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "Old.Ns",
                NewName = "New.Ns",
                UpdateFolders = true
            }));

        Assert.Equal(ErrorCodes.FolderContainsProjectFile, ex.ErrorCode);
        Assert.Equal("3140", ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalProject, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.True(File.Exists(workspace.ProjectPath));
        Assert.True(File.Exists(workspace.SourcePath));
        Assert.False(Directory.Exists(workspace.GetPath("New/Ns")));
        Assert.Contains("namespace Old.Ns;", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_UpdateFoldersTrue_WildcardCompileItem_RewritesPattern()
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

        await using var workspace = await TempWorkspace.CreateWithCompileIncludesAsync(
            ["Old/Ns/**/*.cs", "Consumer.cs"],
            ("Old/Ns/Foo.cs", source),
            ("Consumer.cs", consumer));
        var newFile = workspace.GetPath("New/Ns/Foo.cs");
        var originalProject = await File.ReadAllTextAsync(workspace.ProjectPath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var preview = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = true,
            Preview = true
        });

        Assert.True(preview.Success);
        Assert.True(preview.Preview);
        Assert.Equal(originalProject, await File.ReadAllTextAsync(workspace.ProjectPath));
        Assert.True(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));
        Assert.False(File.Exists(newFile));
        Assert.Contains("Old/Ns/**/*.cs", await File.ReadAllTextAsync(workspace.ProjectPath));

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Old.Ns",
            NewName = "New.Ns",
            UpdateFolders = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(newFile));
        Assert.False(File.Exists(workspace.GetPath("Old/Ns/Foo.cs")));

        var csproj = await File.ReadAllTextAsync(workspace.ProjectPath);
        Assert.Contains("New/Ns/**/*.cs", csproj);
        Assert.DoesNotContain("Old/Ns/**/*.cs", csproj);
        Assert.Contains("Consumer.cs", csproj);

        workspace.Context.Dispose();
        var provider = new MSBuildWorkspaceProvider();
        using var reloaded = await provider.CreateContextAsync(workspace.ProjectPath);
        Assert.NotNull(reloaded.GetDocumentByPath(newFile));
        Assert.Null(reloaded.GetDocumentByPath(workspace.GetPath("Old/Ns/Foo.cs")));
    }

    #endregion

    #region Covering-span column

    private const string SameLineFooSource = """
        namespace A.Foo { public class X {} }namespace B.Foo { public class Y {} }
        """;

    private const string NestedFooSource = """
        namespace A.Foo
        {
            namespace B.Foo
            {
                public class Y {}
            }
        }
        """;

    private const string IndentedNamespaceSource = """
        class Outside {}
            namespace Foo
            {
                public class X {}
            }
        """;

    [Fact]
    public void FindNamespace_ColumnPicksNameCoverage()
    {
        var (root, model) = Parse(SameLineFooSource);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");
        var first = RenameNamespaceOperation.FindNamespace(
            root, model, "Foo", line, ColumnOf(SameLineFooSource, "A.Foo"));
        var second = RenameNamespaceOperation.FindNamespace(
            root, model, "Foo", line, ColumnOf(SameLineFooSource, "B.Foo"));

        Assert.Equal("A.Foo", RenameNamespaceOperation.GetFullName(first));
        Assert.Equal("B.Foo", RenameNamespaceOperation.GetFullName(second));
    }

    [Fact]
    public void FindNamespace_OmittedColumn_SameLineDifferentNames_ThrowsSymbolAmbiguous()
    {
        var (root, model) = Parse(SameLineFooSource);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");

        // Omitted column keeps today's line pick: several different names
        // that share a covering line stay SymbolAmbiguous — do not
        // FirstOrDefault the first same-line declaration.
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, column: null));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
    }

    [Fact]
    public void FindNamespace_OmittedColumn_IndentedDeclaration_DoesNotForceColumn1()
    {
        var (root, model) = Parse(IndentedNamespaceSource);
        var line = FindLine(IndentedNamespaceSource, "namespace Foo");
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Single();
        var startCol = ns.GetLocation().GetLineSpan().StartLinePosition.Character + 1;
        Assert.True(startCol > 1);

        // Omitted column keeps today's line-only covering-span pick. Forcing
        // column 1 would miss this indented declaration.
        var found = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, column: null);
        Assert.Equal("Foo", RenameNamespaceOperation.GetFullName(found));
    }

    [Fact]
    public void FindNamespace_OmittedColumn_LineInsideBody_StillPicks()
    {
        var (root, model) = Parse(NestedFooSource);
        var bodyLine = FindLine(NestedFooSource, "public class Y");

        // Unique simple name on this source is not unique — A.Foo and B.Foo
        // both cover the inner body line. Omitted column keeps today's
        // ambiguity rather than rewriting line-only to covering-span pick.
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameNamespaceOperation.FindNamespace(root, model, "Foo", bodyLine, column: null));
        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);

        var uniqueSource = """
            namespace Foo
            {
                public class X {}
            }
            """;
        var (uniqueRoot, uniqueModel) = Parse(uniqueSource);
        var uniqueBodyLine = FindLine(uniqueSource, "public class X");
        var found = RenameNamespaceOperation.FindNamespace(
            uniqueRoot, uniqueModel, "Foo", uniqueBodyLine, column: null);
        Assert.Equal("Foo", RenameNamespaceOperation.GetFullName(found));
    }

    [Fact]
    public void FindNamespace_ColumnPrefersNameThenSmallestDeclaration()
    {
        var (root, model) = Parse(NestedFooSource);
        var outerLine = FindLine(NestedFooSource, "namespace A.Foo");
        var innerLine = FindLine(NestedFooSource, "namespace B.Foo");
        var bodyLine = FindLine(NestedFooSource, "public class Y");

        var byOuterName = RenameNamespaceOperation.FindNamespace(
            root, model, "Foo", outerLine, ColumnOf(NestedFooSource, "A.Foo") + "A.".Length);
        var byInnerName = RenameNamespaceOperation.FindNamespace(
            root, model, "Foo", innerLine, ColumnOf(NestedFooSource, "B.Foo") + "B.".Length);
        var byInnerBody = RenameNamespaceOperation.FindNamespace(
            root, model, "Foo", bodyLine, ColumnOf(NestedFooSource, "public class Y"));

        Assert.Equal("A.Foo", RenameNamespaceOperation.GetFullName(byOuterName));
        Assert.Equal("A.Foo.B.Foo", RenameNamespaceOperation.GetFullName(byInnerName));
        Assert.Equal("A.Foo.B.Foo", RenameNamespaceOperation.GetFullName(byInnerBody));
    }

    [Fact]
    public void FindNamespace_Column_RepeatedSegment_PicksMatchingIdentifier()
    {
        const string source = "namespace Foo.Bar.Foo { public class X {} }";
        var (root, model) = Parse(source);
        var line = FindLine(source, "namespace Foo.Bar.Foo");
        var firstFoo = ColumnOf(source, "Foo.Bar.Foo");
        var lastFoo = firstFoo + "Foo.Bar.".Length;

        var outer = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, firstFoo);
        var inner = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, lastFoo);

        Assert.Equal("Foo", RenameNamespaceOperation.GetFullName(outer));
        Assert.Equal("Foo.Bar.Foo", RenameNamespaceOperation.GetFullName(inner));
    }

    [Fact]
    public void FindNamespace_Column_RepeatedSegment_NonFooToken_PicksDeclared()
    {
        const string source = "namespace Foo.Bar.Foo { public class X {} }";
        var (root, model) = Parse(source);
        var line = FindLine(source, "namespace Foo.Bar.Foo");
        var bar = ColumnOf(source, "Bar.Foo");

        // Bar is not a Foo segment. Name misses; the declared symbol of
        // this declaration wins over the ancestor that shares the span.
        var found = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, bar);
        Assert.Equal("Foo.Bar.Foo", RenameNamespaceOperation.GetFullName(found));
    }

    [Fact]
    public void FindNamespace_AdjacentNamespaces_ExclusiveEndDoesNotStealNext()
    {
        var (root, model) = Parse(SameLineFooSource);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");
        var secondStart = ColumnOf(SameLineFooSource, "namespace B.Foo");
        var secondName = ColumnOf(SameLineFooSource, "B.Foo");

        var atSecondStart = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, secondStart);
        var atSecondName = RenameNamespaceOperation.FindNamespace(root, model, "Foo", line, secondName);
        var firstAtSecondStart = Assert.Throws<RefactoringException>(() =>
            RenameNamespaceOperation.FindNamespace(root, model, "A.Foo", line, secondStart));

        Assert.Equal("B.Foo", RenameNamespaceOperation.GetFullName(atSecondStart));
        Assert.Equal("B.Foo", RenameNamespaceOperation.GetFullName(atSecondName));
        Assert.Equal(ErrorCodes.SymbolNotFound, firstAtSecondStart.ErrorCode);
    }

    [Fact]
    public void FindNamespace_ColumnWithoutLine_KeepsOmittedLinePath()
    {
        const string source = """
            namespace A.Foo { public class X {} }
            namespace B.Foo { public class Y {} }
            """;
        var (root, model) = Parse(source);
        var column = ColumnOf(source, "A.Foo");

        // Column without line cannot disambiguate across lines — today's
        // omitted-line SymbolAmbiguous. Do not invent column-only pick.
        var ex = Assert.Throws<RefactoringException>(() =>
            RenameNamespaceOperation.FindNamespace(root, model, "Foo", line: null, column));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal("2004", ex.ErrorCode);
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "namespace A { class X {} }namespace B { class Y {} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var ns = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .First(n => n.Name.ToString() == "A");
        var span = ns.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(RenameNamespaceOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(RenameNamespaceOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(RenameNamespaceOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(RenameNamespaceOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [Fact]
    public void SpanCoversLine_WithColumn_TreatsEndAsExclusive()
    {
        const string source = "namespace A { class X {} }namespace B { class Y {} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var ns = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .First(n => n.Name.ToString() == "A");
        var span = ns.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(RenameNamespaceOperation.SpanCoversLine(span, line, startCol));
        Assert.True(RenameNamespaceOperation.SpanCoversLine(span, line, endCol - 1));
        Assert.False(RenameNamespaceOperation.SpanCoversLine(span, line, endCol));
        Assert.False(RenameNamespaceOperation.SpanCoversLine(span, line, startCol - 1));

        const string multiLineSource = """
            namespace A
            {
                class X {}
            }
            """;
        var multiLineTree = CSharpSyntaxTree.ParseText(multiLineSource);
        var multiLineNs = multiLineTree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
            .First(n => n.Name.ToString() == "A");
        var multiLineSpan = multiLineNs.GetLocation().GetLineSpan();
        var startLine = multiLineSpan.StartLinePosition.Line + 1;
        var endLine = multiLineSpan.EndLinePosition.Line + 1;

        for (var coveredLine = startLine; coveredLine <= endLine; coveredLine++)
            Assert.True(RenameNamespaceOperation.SpanCoversLine(multiLineSpan, coveredLine, column: null));

        Assert.False(RenameNamespaceOperation.SpanCoversLine(multiLineSpan, startLine - 1, column: null));
        Assert.False(RenameNamespaceOperation.SpanCoversLine(multiLineSpan, endLine + 1, column: null));
    }

    [SkippableFact]
    public async Task RenameNamespace_OmittedColumn_SameLine_ThrowsSymbolAmbiguous()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineFooSource, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "Foo",
                NewName = "B.Renamed",
                Line = line
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_Column_SelectsSecondNamespaceOnSameLine()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", SameLineFooSource),
            ("Consumer.cs", """
                namespace Other;
                public class Consumer
                {
                    public B.Foo.Y Create() => new B.Foo.Y();
                    public A.Foo.X Other() => new A.Foo.X();
                }
                """));
        var operation = new RenameNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Foo",
            NewName = "B.Renamed",
            Line = line,
            Column = ColumnOf(SameLineFooSource, "B.Foo")
        });

        Assert.True(result.Success);
        var declaration = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("namespace A.Foo", declaration);
        Assert.Contains("namespace B.Renamed", declaration);
        Assert.DoesNotContain("namespace B.Foo", declaration);
        Assert.DoesNotContain("namespace A.Renamed", declaration);

        var usages = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        Assert.Contains("B.Renamed.Y", usages);
        Assert.Contains("A.Foo.X", usages);
        Assert.DoesNotContain("B.Foo.Y", usages);
    }

    [SkippableFact]
    public async Task RenameNamespace_AdjacentNamespaces_ColumnOnSecondDoesNotRenameFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineFooSource, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");
        var secondStart = ColumnOf(SameLineFooSource, "namespace B.Foo");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "A.Foo",
                NewName = "Renamed",
                Line = line,
                Column = secondStart
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_Preview_Column_DescribesRewriteAndWritesNothing()
    {
        const string consumer = """
            namespace Other;
            public class Consumer
            {
                public B.Foo.Y Create() => new B.Foo.Y();
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Foo.cs", SameLineFooSource),
            ("Consumer.cs", consumer));
        var originalSource = await File.ReadAllTextAsync(workspace.SourcePath);
        var originalConsumer = await File.ReadAllTextAsync(workspace.SecondarySourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);
        var line = FindLine(SameLineFooSource, "namespace A.Foo");

        var result = await operation.ExecuteAsync(new RenameNamespaceParams
        {
            SourceFile = workspace.SourcePath,
            NamespaceName = "Foo",
            NewName = "B.Renamed",
            Line = line,
            Column = ColumnOf(SameLineFooSource, "B.Foo"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains(result.PendingChanges, c =>
            c.Description != null &&
            c.Description.Contains("B.Foo", StringComparison.Ordinal) &&
            c.Description.Contains("B.Renamed", StringComparison.Ordinal));
        Assert.DoesNotContain(result.PendingChanges, c =>
            c.Description != null &&
            c.Description.Contains("A.Foo", StringComparison.Ordinal) &&
            c.Description.Contains("A.Renamed", StringComparison.Ordinal));
        Assert.Equal(originalSource, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(originalConsumer, await File.ReadAllTextAsync(workspace.SecondarySourcePath));
        Assert.Contains("namespace A.Foo", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("namespace B.Foo", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("B.Foo.Y", await File.ReadAllTextAsync(workspace.SecondarySourcePath));
    }

    [SkippableFact]
    public async Task RenameNamespace_ColumnWithoutLine_SameNameAcrossLines_ThrowsSymbolAmbiguous()
    {
        const string source = """
            namespace A.Foo { public class X {} }
            namespace B.Foo { public class Y {} }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source, "Foo.cs");
        var original = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new RenameNamespaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RenameNamespaceParams
            {
                SourceFile = workspace.SourcePath,
                NamespaceName = "Foo",
                NewName = "Renamed",
                Column = ColumnOf(source, "A.Foo")
            }));

        Assert.Equal(ErrorCodes.SymbolAmbiguous, ex.ErrorCode);
        Assert.Equal(original, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static RenameNamespaceParams ValidParams(
        string? sourceFile = null,
        string namespaceName = "OldNs",
        string newName = "NewNs",
        int? line = null,
        int? column = null,
        bool updateFolders = false) => new()
        {
            SourceFile = sourceFile ?? Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNsMissing.cs"),
            NamespaceName = namespaceName,
            NewName = newName,
            Line = line,
            Column = column,
            UpdateFolders = updateFolders
        };

    private static (SyntaxNode Root, SemanticModel Model) Parse(string source)
    {
        var compilation = CreateCompilation(source);
        var tree = compilation.SyntaxTrees.Single();
        return (tree.GetRoot(), compilation.GetSemanticModel(tree));
    }

    private static int FindLine(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet '{snippet}' not found.");
        return source[..index].Count(c => c == '\n') + 1;
    }

    private static int ColumnOf(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snippet '{snippet}' not found.");
        var lineStart = source.LastIndexOf('\n', index) + 1;
        return index - lineStart + 1;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            "RenameNamespaceCollisionTest",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    }

    private static INamespaceSymbol FindCompilationNamespace(Compilation compilation, string fullName)
    {
        INamespaceSymbol current = compilation.GlobalNamespace;
        foreach (var segment in fullName.Split('.'))
        {
            current = current.GetNamespaceMembers().Single(n => n.Name == segment);
        }

        return current;
    }

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
            return CreateAtProjectPathAsync("TestApp.csproj", projectXml, files);
        }

        public static Task<TempWorkspace> CreateWithProjectInFolderAsync(
            string projectRelativeDirectory,
            params (string FileName, string Source)[] files)
        {
            var relativeProject = Path.Combine(
                projectRelativeDirectory.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar),
                "TestApp.csproj");
            return CreateAtProjectPathAsync(
                relativeProject,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
                files);
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

        public static Task<TempWorkspace> CreateAsync(
            string projectXml,
            params (string FileName, string Source)[] files) =>
            CreateAtProjectPathAsync("TestApp.csproj", projectXml, files);

        public static async Task<TempWorkspace> CreateAtProjectPathAsync(
            string projectRelativePath,
            string projectXml,
            params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var relativeProject = projectRelativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var projectPath = Path.Combine(directory, relativeProject);
            var projectParent = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(projectParent))
                Directory.CreateDirectory(projectParent);
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

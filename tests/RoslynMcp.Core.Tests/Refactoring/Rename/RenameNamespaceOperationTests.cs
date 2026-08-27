using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRenameNs_" + Guid.NewGuid().ToString("N"));
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

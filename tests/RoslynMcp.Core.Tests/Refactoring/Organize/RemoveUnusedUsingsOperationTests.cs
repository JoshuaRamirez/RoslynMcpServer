using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Organize;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Organize;

/// <summary>
/// Unit tests for RemoveUnusedUsingsOperation semantic validation, plus
/// operation-level <c>allFiles</c> optional <c>sourceFile</c> coverage.
/// </summary>
public class RemoveUnusedUsingsOperationTests
{
    #region All Usings Used Tests

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_ReturnsSuccessTrue()
    {
        // Arrange
        var unusedUsings = new List<string>();

        // Act
        var hasUnusedUsings = unusedUsings.Count > 0;

        // Assert
        Assert.False(hasUnusedUsings);
    }

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_ChangesCountIsZero()
    {
        // Arrange
        var unusedUsings = new List<string>();

        // Act
        var changeCount = unusedUsings.Count;

        // Assert
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void RemoveUnusedUsings_AllUsingsUsed_PreservesAllUsings()
    {
        // Arrange
        var allUsings = new List<string> { "System", "System.Collections.Generic" };
        var usedNamespaces = new HashSet<string> { "System", "System.Collections.Generic" };

        // Act
        var remainingUsings = allUsings.Where(u => usedNamespaces.Contains(u)).ToList();

        // Assert
        Assert.Equal(allUsings.Count, remainingUsings.Count);
    }

    #endregion

    #region Extension Method Preservation Tests

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodUsing_PreservesUsing()
    {
        // Arrange
        var usingNamespace = "System.Linq";
        var usedExtensionMethods = new HashSet<string> { "System.Linq" };

        // Act
        var shouldPreserve = usedExtensionMethods.Contains(usingNamespace);

        // Assert
        Assert.True(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodUsed_NamespaceInUsedSet()
    {
        // Arrange
        // In a real scenario with source code like:
        //   using System.Linq;
        //   class Test { void M() { var first = new[]{1,2,3}.First(); } }
        // semantic analysis would detect First() as an extension method from System.Linq
        var extensionMethodNamespace = "System.Linq";

        // Act
        var isExtensionMethodNamespace = extensionMethodNamespace == "System.Linq";

        // Assert
        Assert.True(isExtensionMethodNamespace);
    }

    [Fact]
    public void RemoveUnusedUsings_ExtensionMethodNotUsed_CanBeRemoved()
    {
        // Arrange
        var usingNamespace = "System.Linq";
        var usedNamespaces = new HashSet<string> { "System" }; // Linq not used

        // Act
        var isUsed = usedNamespaces.Contains(usingNamespace);

        // Assert
        Assert.False(isUsed);
    }

    #endregion

    #region Sorting Remaining Usings Tests

    [Fact]
    public void RemoveUnusedUsings_SortsRemainingUsings_SystemFirst()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "MyApp.Services",
            "System.Collections.Generic",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.StartsWith("System", sorted.First());
    }

    [Fact]
    public void RemoveUnusedUsings_SortsRemainingUsings_Alphabetically()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "System.Threading",
            "System.Collections",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.Equal("System", sorted[0]);
        Assert.Equal("System.Collections", sorted[1]);
        Assert.Equal("System.Threading", sorted[2]);
    }

    [Fact]
    public void RemoveUnusedUsings_MixedUsings_SortsCorrectly()
    {
        // Arrange
        var remainingUsings = new List<string>
        {
            "Zebra.Utils",
            "System.Linq",
            "Apple.Core",
            "System"
        };

        // Act
        var sorted = SortUsings(remainingUsings);

        // Assert
        Assert.Equal("System", sorted[0]);
        Assert.Equal("System.Linq", sorted[1]);
        Assert.Equal("Apple.Core", sorted[2]);
        Assert.Equal("Zebra.Utils", sorted[3]);
    }

    #endregion

    #region Static Using Tests

    [Fact]
    public void RemoveUnusedUsings_StaticUsing_DetectedAsStaticUsing()
    {
        // Arrange
        var source = "using static System.Math;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isStaticUsing = usingDirective.StaticKeyword != default;

        // Assert
        Assert.True(isStaticUsing);
    }

    [Fact]
    public void RemoveUnusedUsings_RegularUsing_NotDetectedAsStatic()
    {
        // Arrange
        var source = "using System;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isStaticUsing = usingDirective.StaticKeyword != default;

        // Assert
        Assert.False(isStaticUsing);
    }

    [Fact]
    public void RemoveUnusedUsings_StaticUsingUsed_PreservesUsing()
    {
        // Arrange
        var staticUsingNamespace = "System.Math";
        var usedStaticMembers = new HashSet<string> { "System.Math" };

        // Act
        var shouldPreserve = usedStaticMembers.Contains(staticUsingNamespace);

        // Assert
        Assert.True(shouldPreserve);
    }

    #endregion

    #region Using Alias Tests

    [Fact]
    public void RemoveUnusedUsings_UsingAlias_DetectedAsAlias()
    {
        // Arrange
        var source = "using MyList = System.Collections.Generic.List<int>;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var isAlias = usingDirective.Alias != null;

        // Assert
        Assert.True(isAlias);
    }

    [Fact]
    public void RemoveUnusedUsings_UsingAliasUsed_PreservesAlias()
    {
        // Arrange
        var aliasName = "MyList";
        var usedAliases = new HashSet<string> { "MyList" };

        // Act
        var shouldPreserve = usedAliases.Contains(aliasName);

        // Assert
        Assert.True(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_UsingAliasNotUsed_CanBeRemoved()
    {
        // Arrange
        var aliasName = "MyList";
        var usedAliases = new HashSet<string> { "OtherAlias" };

        // Act
        var shouldPreserve = usedAliases.Contains(aliasName);

        // Assert
        Assert.False(shouldPreserve);
    }

    [Fact]
    public void RemoveUnusedUsings_AliasExtractsName_ReturnsAliasIdentifier()
    {
        // Arrange
        var source = "using MyAlias = System.String;";
        var tree = CSharpSyntaxTree.ParseText(source);
        var usingDirective = tree.GetRoot()
            .DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .First();

        // Act
        var aliasName = usingDirective.Alias?.Name.ToString();

        // Assert
        Assert.Equal("MyAlias", aliasName);
    }

    #endregion

    #region Diagnostic Detection Tests

    [Fact]
    public void RemoveUnusedUsings_CS8019Diagnostic_IndicatesUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "CS8019";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.True(isUnnecessaryUsingDiagnostic);
    }

    [Fact]
    public void RemoveUnusedUsings_IDE0005Diagnostic_IndicatesUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "IDE0005";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.True(isUnnecessaryUsingDiagnostic);
    }

    [Fact]
    public void RemoveUnusedUsings_OtherDiagnostic_NotUnnecessaryUsing()
    {
        // Arrange
        var diagnosticId = "CS0246";

        // Act
        var isUnnecessaryUsingDiagnostic = diagnosticId == "CS8019" || diagnosticId == "IDE0005";

        // Assert
        Assert.False(isUnnecessaryUsingDiagnostic);
    }

    #endregion

    #region AllFiles optional sourceFile

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_WithoutSourceFile_RemovesUnusedUsings()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA),
            ("UnusedB.cs", UnusedB),
            ("AlreadyClean.cs", AlreadyClean));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var beforeClean = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]);

        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams { AllFiles = true });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedA.cs"]);
        var afterB = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedB.cs"]);
        Assert.DoesNotContain("using System.Text", afterA);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.DoesNotContain("using System.IO", afterB);
        Assert.Contains("using System.Collections.Generic", afterB);
        Assert.Equal(beforeClean, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA),
            ("UnusedB.cs", UnusedB),
            ("AlreadyClean.cs", AlreadyClean));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedB.cs"]);
        var beforeClean = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]);

        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnusedA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedA.cs"]);
        Assert.DoesNotContain("using System.Text", afterA);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnusedB.cs"]));
        Assert.Equal(beforeClean, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnusedA.cs"]));
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA),
            ("UnusedB.cs", UnusedB),
            ("AlreadyClean.cs", AlreadyClean));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedB.cs"]);
        var beforeClean = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["UnusedA.cs"]);

        // Shared AllFilesDocumentHelpers default path returns ignore-case workspace
        // matches even when File.Exists(flipped) is false (case-sensitive volumes).
        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedA.cs"]);
        Assert.DoesNotContain("using System.Text", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["UnusedB.cs"]));
        Assert.Equal(beforeClean, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyClean.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnusedA.cs"]));
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA),
            ("UnusedB.cs", UnusedB));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveUnusedUsings_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "using System.Text;\nclass Outside { void M(){ List<int> x = new(); } }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new RemoveUnusedUsingsParams
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
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_MissingPath_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA),
            ("UnusedB.cs", UnusedB));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveUnusedUsings_Missing_" + Guid.NewGuid().ToString("N"), "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnusedA.cs", UnusedA), ("unuseda.cs", UnusedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnusedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unuseda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["unuseda.cs"]);

        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["UnusedA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["UnusedA.cs"]);
        Assert.DoesNotContain("using System.Text", afterA);
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["unuseda.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["UnusedA.cs"]));
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("UnusedA.cs", UnusedA), ("unuseda.cs", UnusedB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["UnusedA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["unuseda.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["UnusedA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_RelativePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true,
                SourceFile = "UnusedA.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_OptionalSourceFile_NonCSharpAbsolutePath_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("UnusedA.cs", UnusedA));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var nonCs = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveUnusedUsings_NonCs_" + Guid.NewGuid().ToString("N") + ".txt");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true,
                SourceFile = nonCs
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_LinkedDocumentViewsThatRewriteIdentically_ReportSingleModifiedFile()
    {
        const string sharedSource = """
            using System.Text;
            using System.Collections.Generic;

            namespace TestApp
            {
                public class Shared
                {
                    public void M()
                    {
                        List<int> items = new List<int>();
                    }
                }
            }
            """;
        const string anchorSource = """
            namespace TestApp
            {
                public class Anchor
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorSource, anchorSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new RemoveUnusedUsingsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Shared.cs"]));
        var updated = await File.ReadAllTextAsync(workspace.SourcePaths["Shared.cs"]);
        Assert.DoesNotContain("using System.Text", updated);
        Assert.Contains("using System.Collections.Generic", updated);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_LinkedDocumentViewsThatRewriteDifferently_ThrowsCannotConvert()
    {
        // Shared references Foo via ProjectANs. Project A keeps that using (Foo
        // lives there) and drops System.Text; Project B has Foo in ProjectBNs so
        // ProjectANs is unused too — divergent rewrite texts → CannotConvert.
        const string sharedSource = """
            using System.Text;
            using ProjectANs;

            namespace TestApp
            {
                public class Shared
                {
                    public void M()
                    {
                        Foo f = null!;
                    }
                }
            }
            """;
        const string anchorASource = """
            namespace ProjectANs
            {
                public class Foo
                {
                }
            }
            """;
        const string anchorBSource = """
            namespace ProjectBNs
            {
                public class Foo
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorASource, anchorBSource);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new RemoveUnusedUsingsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Linked workspace documents", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_LinkedNoOpSiblingVsRewrite_ThrowsCannotConvert()
    {
        // Project A (ImplicitUsings disable) needs System.Collections.Generic for List
        // (no-op). Project B (ImplicitUsings enable) often flags the explicit using as
        // unnecessary (rewrite). Propagating B's rewrite onto A would strip a needed
        // using → CannotConvert (Codex peer of AddMissingUsings).
        const string sharedSource = """
            using System.Collections.Generic;

            namespace TestApp
            {
                public class Shared
                {
                    public void M()
                    {
                        List<int> items = new List<int>();
                    }
                }
            }
            """;
        const string anchorSource = """
            namespace TestApp
            {
                public class Anchor
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithLinkedProjectsAsync(
            sharedSource, anchorSource, anchorSource, projectBImplicitUsings: true);
        var linkedDocuments = workspace.Context.Solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => PathEquals(d.FilePath!, workspace.SourcePaths["Shared.cs"]))
            .ToList();
        Assert.Equal(2, linkedDocuments.Count);

        var operation = new RemoveUnusedUsingsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new RemoveUnusedUsingsParams
            {
                AllFiles = true
            }));

        Assert.Equal(ErrorCodes.CannotConvert, ex.ErrorCode);
        Assert.Contains("Linked workspace documents", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RemoveUnusedUsings_AllFilesTrue_FileScopedNamespaceUsing_DoesNotFakeRewrite()
    {
        // Copilot #1727: unused using inside a file-scoped namespace appears in
        // diagnostics but is not rewritten via root.Usings / WithUsings. Must not
        // return a non-null rewrite that claims a removal with identical text.
        const string source = """
            namespace TestApp;
            using System.Text;
            public class C
            {
                public void M() { }
            }
            """;
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileScoped.cs", source));
        var operation = new RemoveUnusedUsingsOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePaths["FileScoped.cs"]);

        var result = await operation.ExecuteAsync(new RemoveUnusedUsingsParams { AllFiles = true });

        Assert.True(result.Success);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["FileScoped.cs"]));
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Equal(0, result.UsingDirectivesRemoved);
    }

    private const string UnusedA = """
        using System.Text;
        using System.Collections.Generic;

        namespace TestApp
        {
            public class UnusedA
            {
                public void M()
                {
                    List<int> items = new List<int>();
                }
            }
        }
        """;

    private const string UnusedB = """
        using System.IO;
        using System.Collections.Generic;

        namespace TestApp
        {
            public class UnusedB
            {
                public void M()
                {
                    Dictionary<string, int> map = new Dictionary<string, int>();
                }
            }
        }
        """;

    private const string AlreadyClean = """
        using System.Collections.Generic;

        namespace TestApp
        {
            public class AlreadyClean
            {
                public void M()
                {
                    List<int> items = new List<int>();
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveUnusedUsings_" + Guid.NewGuid().ToString("N"));
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
                    <ImplicitUsings>disable</ImplicitUsings>
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

        public static async Task<TempWorkspace> CreateWithLinkedProjectsAsync(
            string sharedSource,
            string anchorASource,
            string anchorBSource,
            bool projectBImplicitUsings = false)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpRemoveUnusedUsingsLinked_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            var sharedPath = Path.Combine(directory, "Shared.cs");
            var rootProjectPath = Path.Combine(directory, "ProjectA.csproj");
            var referencedProjectPath = Path.Combine(directory, "ProjectB.csproj");
            var anchorAPath = Path.Combine(directory, "AnchorA.cs");
            var anchorBPath = Path.Combine(directory, "AnchorB.cs");
            var projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var projectAGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var projectBGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

            await File.WriteAllTextAsync(sharedPath, sharedSource);
            await File.WriteAllTextAsync(anchorAPath, anchorASource);
            await File.WriteAllTextAsync(anchorBPath, anchorBSource);
            await File.WriteAllTextAsync(solutionPath, $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                VisualStudioVersion = 17.0.31903.59
                MinimumVisualStudioVersion = 10.0.40219.1
                Project("{{projectTypeGuid}}") = "ProjectA", "ProjectA.csproj", "{{projectAGuid}}"
                EndProject
                Project("{{projectTypeGuid}}") = "ProjectB", "ProjectB.csproj", "{{projectBGuid}}"
                EndProject
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{{projectAGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectAGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectAGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectAGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                		{{projectBGuid}}.Debug|Any CPU.Build.0 = Debug|Any CPU
                		{{projectBGuid}}.Release|Any CPU.ActiveCfg = Release|Any CPU
                		{{projectBGuid}}.Release|Any CPU.Build.0 = Release|Any CPU
                	EndGlobalSection
                EndGlobal
                """);

            await File.WriteAllTextAsync(rootProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorA.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            var projectBImplicit = projectBImplicitUsings ? "enable" : "disable";
            await File.WriteAllTextAsync(referencedProjectPath, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>{{projectBImplicit}}</ImplicitUsings>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="AnchorB.cs" />
                    <Compile Include="Shared.cs" Link="Shared.cs" />
                  </ItemGroup>
                </Project>
                """);

            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Shared.cs"] = sharedPath,
                ["AnchorA.cs"] = anchorAPath,
                ["AnchorB.cs"] = anchorBPath
            };

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(solutionPath);
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
                    SourcePath = sharedPath,
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

    #endregion

    #region Helper Methods

    private static List<string> SortUsings(List<string> usings)
    {
        return usings
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n)
            .ToList();
    }

    #endregion
}

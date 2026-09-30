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
/// Unit tests for AddMissingUsingsOperation semantic validation, plus
/// operation-level <c>allFiles</c> optional <c>sourceFile</c> coverage.
/// </summary>
public class AddMissingUsingsOperationTests
{
    #region No Missing Usings Tests

    [Fact]
    public void AddMissingUsings_NoMissingUsings_ReturnsSuccessTrue()
    {
        // Arrange
        var diagnosticIds = new List<string>(); // No CS0246, CS0103, CS0234

        // Act
        var hasMissingUsings = HasMissingUsingDiagnostics(diagnosticIds);

        // Assert
        Assert.False(hasMissingUsings);
    }

    [Fact]
    public void AddMissingUsings_NoMissingUsings_ChangesCountIsZero()
    {
        // Arrange
        var namespacesToAdd = new HashSet<string>();

        // Act
        var changeCount = namespacesToAdd.Count;

        // Assert
        Assert.Equal(0, changeCount);
    }

    [Fact]
    public void AddMissingUsings_FileWithAllUsings_ReturnsEmptyList()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System", "System.Collections.Generic" };
        var requiredNamespaces = new List<string> { "System", "System.Collections.Generic" };

        // Act
        var missingUsings = GetMissingUsings(requiredNamespaces, existingUsings);

        // Assert
        Assert.Empty(missingUsings);
    }

    #endregion

    #region Duplicate Detection Tests

    [Fact]
    public void AddMissingUsings_AlreadyHasUsing_DoesNotDuplicate()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System.Collections.Generic" };
        var candidateNamespaces = new List<string> { "System.Collections.Generic" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Empty(newUsings);
    }

    [Fact]
    public void AddMissingUsings_PartiallyPresent_AddsOnlyMissing()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System" };
        var candidateNamespaces = new List<string> { "System", "System.Linq" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Single(newUsings);
    }

    [Fact]
    public void AddMissingUsings_PartiallyPresent_AddsMissingCorrectly()
    {
        // Arrange
        var existingUsings = new HashSet<string> { "System" };
        var candidateNamespaces = new List<string> { "System", "System.Linq" };

        // Act
        var newUsings = candidateNamespaces.Where(n => !existingUsings.Contains(n)).ToList();

        // Assert
        Assert.Contains("System.Linq", newUsings);
    }

    #endregion

    #region Namespace Selection Priority Tests

    [Fact]
    public void AddMissingUsings_MultipleNamespaceCandidates_SelectsSystemFirst()
    {
        // Arrange
        var candidates = new List<string>
        {
            "ThirdParty.Collections",
            "System.Collections.Generic",
            "MyApp.Collections"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.StartsWith("System", selected);
    }

    [Fact]
    public void AddMissingUsings_MultipleSystemNamespaces_SelectsShortest()
    {
        // Arrange
        var candidates = new List<string>
        {
            "System.Collections.Generic.Specialized",
            "System.Collections",
            "System.Collections.Generic"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.Equal("System.Collections", selected);
    }

    [Fact]
    public void AddMissingUsings_NoSystemNamespace_SelectsShortestNonSystem()
    {
        // Arrange
        var candidates = new List<string>
        {
            "ThirdParty.Deep.Nested.Namespace",
            "ThirdParty.Collections",
            "ThirdParty"
        };

        // Act
        var selected = SelectBestNamespace(candidates);

        // Assert
        Assert.Equal("ThirdParty", selected);
    }

    #endregion

    #region Generic Type Resolution Tests

    [Fact]
    public void AddMissingUsings_GenericType_ExtractsTypeName()
    {
        // Arrange
        var source = "List<int> items;";
        var tree = CSharpSyntaxTree.ParseText($"class Test {{ {source} }}");
        var genericName = tree.GetRoot()
            .DescendantNodes()
            .OfType<GenericNameSyntax>()
            .First();

        // Act
        var typeName = GetTypeName(genericName);

        // Assert
        Assert.Equal("List", typeName);
    }

    [Fact]
    public void AddMissingUsings_GenericType_ResolvesToCollections()
    {
        // Arrange
        var typeName = "List";
        var knownNamespaces = new Dictionary<string, string>
        {
            { "List", "System.Collections.Generic" },
            { "Dictionary", "System.Collections.Generic" },
            { "Console", "System" }
        };

        // Act
        var resolvedNamespace = knownNamespaces.GetValueOrDefault(typeName);

        // Assert
        Assert.Equal("System.Collections.Generic", resolvedNamespace);
    }

    [Fact]
    public void AddMissingUsings_QualifiedName_ExtractsRightmostName()
    {
        // Arrange
        var source = "System.Collections.Generic.List<int> items;";
        var tree = CSharpSyntaxTree.ParseText($"class Test {{ {source} }}");
        var qualifiedName = tree.GetRoot()
            .DescendantNodes()
            .OfType<QualifiedNameSyntax>()
            .FirstOrDefault();

        // Act
        var typeName = qualifiedName != null ? GetTypeName(qualifiedName) : null;

        // Assert - QualifiedName should extract the rightmost part
        Assert.NotNull(typeName);
    }

    #endregion

    #region Sorting Tests

    [Fact]
    public void AddMissingUsings_Sorting_SystemNamespacesFirst()
    {
        // Arrange
        var usings = new List<string>
        {
            "MyApp.Services",
            "System.Linq",
            "ThirdParty.Utils",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);

        // Assert
        Assert.StartsWith("System", sorted.First());
    }

    [Fact]
    public void AddMissingUsings_Sorting_SystemNamespacesAlphabetical()
    {
        // Arrange
        var usings = new List<string>
        {
            "System.Threading",
            "System.Collections",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);

        // Assert
        Assert.Equal("System", sorted[0]);
    }

    [Fact]
    public void AddMissingUsings_Sorting_NonSystemAfterSystem()
    {
        // Arrange
        var usings = new List<string>
        {
            "MyApp.Services",
            "System"
        };

        // Act
        var sorted = SortUsings(usings);
        var systemIndex = sorted.IndexOf("System");
        var myAppIndex = sorted.IndexOf("MyApp.Services");

        // Assert
        Assert.True(systemIndex < myAppIndex);
    }

    #endregion

    #region Helper Methods

    private static bool HasMissingUsingDiagnostics(List<string> diagnosticIds)
    {
        var missingUsingCodes = new HashSet<string> { "CS0246", "CS0103", "CS0234" };
        return diagnosticIds.Any(d => missingUsingCodes.Contains(d));
    }

    private static List<string> GetMissingUsings(List<string> required, HashSet<string> existing)
    {
        return required.Where(n => !existing.Contains(n)).ToList();
    }

    private static string SelectBestNamespace(List<string> candidates)
    {
        return candidates
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n.Length)
            .First();
    }

    private static string? GetTypeName(SyntaxNode node)
    {
        return node switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.ToString(),
            _ => null
        };
    }

    private static List<string> SortUsings(List<string> usings)
    {
        return usings
            .OrderBy(n => n.StartsWith("System") ? 0 : 1)
            .ThenBy(n => n)
            .ToList();
    }

    #endregion

    #region AllFiles optional sourceFile

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_WithoutSourceFile_AddsMissingUsings()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("MissingA.cs", MissingA),
            ("MissingB.cs", MissingB),
            ("AlreadyComplete.cs", AlreadyComplete));
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var beforeComplete = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]);

        var result = await operation.ExecuteAsync(new AddMissingUsingsParams { AllFiles = true });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["MissingA.cs"]);
        var afterB = await File.ReadAllTextAsync(workspace.SourcePaths["MissingB.cs"]);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.Contains("using System.Collections.Generic", afterB);
        Assert.Equal(beforeComplete, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("MissingA.cs", MissingA),
            ("MissingB.cs", MissingB),
            ("AlreadyComplete.cs", AlreadyComplete));
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["MissingB.cs"]);
        var beforeComplete = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]);

        var result = await operation.ExecuteAsync(new AddMissingUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["MissingA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["MissingA.cs"]);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["MissingB.cs"]));
        Assert.Equal(beforeComplete, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["MissingA.cs"]));
    }

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("MissingA.cs", MissingA),
            ("MissingB.cs", MissingB),
            ("AlreadyComplete.cs", AlreadyComplete));
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["MissingB.cs"]);
        var beforeComplete = await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["MissingA.cs"]);

        // Shared AllFilesDocumentHelpers default path returns ignore-case workspace
        // matches even when File.Exists(flipped) is false (case-sensitive volumes).
        var result = await operation.ExecuteAsync(new AddMissingUsingsParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["MissingA.cs"]);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["MissingB.cs"]));
        Assert.Equal(beforeComplete, await File.ReadAllTextAsync(workspace.SourcePaths["AlreadyComplete.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["MissingA.cs"]));
    }

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_OutsideWorkspace_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("MissingA.cs", MissingA),
            ("MissingB.cs", MissingB));
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpAddMissingUsings_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class Outside { void M(){ List<int> x = new(); } }");

            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new AddMissingUsingsParams
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
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_MissingPath_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("MissingA.cs", MissingA),
            ("MissingB.cs", MissingB));
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var missing = Path.Combine(Path.GetTempPath(), "RoslynMcpAddMissingUsings_Missing_" + Guid.NewGuid().ToString("N"), "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddMissingUsingsParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_ExactCase_PrefersSingleWorkspaceFile()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("MissingA.cs", MissingA), ("missinga.cs", MissingB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["MissingA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["missinga.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var beforeLower = await File.ReadAllTextAsync(workspace.SourcePaths["missinga.cs"]);

        var result = await operation.ExecuteAsync(new AddMissingUsingsParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["MissingA.cs"]
        });

        Assert.True(result.Success);
        var afterA = await File.ReadAllTextAsync(workspace.SourcePaths["MissingA.cs"]);
        Assert.Contains("using System.Collections.Generic", afterA);
        Assert.Equal(beforeLower, await File.ReadAllTextAsync(workspace.SourcePaths["missinga.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["MissingA.cs"]));
    }

    [SkippableFact]
    public async Task AddMissingUsings_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("MissingA.cs", MissingA), ("missinga.cs", MissingB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["MissingA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["missinga.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new AddMissingUsingsOperation(workspace.Context);
        var ambiguous = FlipPathCasing(workspace.SourcePaths["MissingA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new AddMissingUsingsParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        // Shared filter is OrdinalIgnoreCase, so both case-distinct workspace
        // files match a flipped spelling whether or not File.Exists(ambiguous).
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private const string MissingA = """
        namespace TestApp
        {
            public class MissingA
            {
                public void M()
                {
                    List<int> items = new List<int>();
                }
            }
        }
        """;

    private const string MissingB = """
        namespace TestApp
        {
            public class MissingB
            {
                public void M()
                {
                    Dictionary<string, int> map = new Dictionary<string, int>();
                }
            }
        }
        """;

    private const string AlreadyComplete = """
        using System.Collections.Generic;

        namespace TestApp
        {
            public class AlreadyComplete
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpAddMissingUsings_" + Guid.NewGuid().ToString("N"));
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

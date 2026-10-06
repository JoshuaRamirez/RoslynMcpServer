using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetTypeHierarchyOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="FindImplementationsOperation"/> / <see cref="GetDiagnosticsOperation"/>)
/// and optional <c>derivedFile</c> (restricts reported DerivedTypes to one file; peer of
/// <c>find_implementations</c> <c>implementationFile</c> / <c>find_references</c> <c>referenceFile</c>).
/// </summary>
public class GetTypeHierarchyOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            """);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Base",
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetTypeHierarchy_OmittedMaxResults_ReturnsFullDerivedListNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            class C : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 derived, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.DerivedTypes.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            class C : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.DerivedTypes.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.DerivedTypes[0].FullyQualifiedName, capped.Data.DerivedTypes[0].FullyQualifiedName);
        Assert.Equal(full.Data.DerivedTypes[1].FullyQualifiedName, capped.Data.DerivedTypes[1].FullyQualifiedName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Base {}
            class A : Base {}
            class B : Base {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Base",
            Direction = "Descendants",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.DerivedTypes.Count);
        Assert.False(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_MaxResultsDoesNotCapBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            interface IMarker {}
            class Mid : System.Object, IMarker {}
            class Leaf : Mid {}
            class Other : Mid {}
            """);

        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mid",
            Direction = "Both"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);
        Assert.NotEmpty(full.Data.Interfaces);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mid",
            Direction = "Both",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Single(capped.Data.DerivedTypes);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(full.Data.BaseTypes.Count, capped.Data.BaseTypes.Count);
        Assert.Equal(full.Data.Interfaces.Count, capped.Data.Interfaces.Count);
        Assert.Equal(
            full.Data.Interfaces.Select(i => i.FullyQualifiedName).ToArray(),
            capped.Data.Interfaces.Select(i => i.FullyQualifiedName).ToArray());
    }

    #endregion

    #region derivedFile filter

    // Shape is declared in Shape.cs (line 1, column 14). It is derived twice in DerivedA.cs
    // (A1, A2), once in DerivedB.cs (B1), and once by partial class Split whose parts live in
    // SplitOne.cs (declares the base) and SplitTwo.cs. Empty.cs has none. IMarker gives Shape an
    // interface and Root gives it a base type so BaseTypes / Interfaces can be checked.
    private static readonly Dictionary<string, string> MultiFileSources = new()
    {
        ["Shape.cs"] = """
            public class Shape : Root, IMarker { }
            public class Root { }
            public interface IMarker { }
            """,
        ["DerivedA.cs"] = """
            public class A1 : Shape { }
            public class A2 : Shape { }
            """,
        ["DerivedB.cs"] = """
            public class B1 : Shape { }
            """,
        ["SplitOne.cs"] = """
            public partial class Split : Shape { }
            """,
        ["SplitTwo.cs"] = """
            // second part of Split
            public partial class Split { }
            """,
        ["Empty.cs"] = """
            public class Unrelated
            {
                public void Nothing() { }
            }
            """
    };

    [Fact]
    public void DerivedFile_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.DerivedFile);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_OmittedDerivedFile_ReturnsDerivedTypesFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2", "B1", "Split" }, result.Data.DerivedTypes.Select(d => d.TypeName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("DerivedA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2" }, result.Data.DerivedTypes.Select(d => d.TypeName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(result.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("DerivedA.cs"))));
        Assert.Equal(new int?[] { 1, 2 }, result.Data.DerivedTypes.Select(d => d.Line).OrderBy(l => l));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("Shape", result.Data.TypeName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFile_LineColumnResolution_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            Line = 1,
            Column = 14,
            Direction = "Descendants",
            DerivedFile = workspace.PathOf("DerivedB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.DerivedTypes);
        Assert.Equal("B1", only.TypeName);
        Assert.True(SamePath(only.File, workspace.PathOf("DerivedB.cs")));
        Assert.Equal(1, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFileIsSecondaryPartialPart_ReportsThatPart()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("SplitTwo.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.DerivedTypes);
        Assert.Equal("Split", only.TypeName);
        // Reported at the part declared in derivedFile, not the first source location.
        Assert.True(SamePath(only.File, workspace.PathOf("SplitTwo.cs")));
        Assert.Equal(2, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFileWithNoDerivedTypes_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.DerivedTypes);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("DerivedA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.DerivedTypes);
        Assert.True(SamePath(only.File, workspace.PathOf("DerivedA.cs")));
        // TotalCount reflects the filtered set (2 in DerivedA.cs), not the solution-wide 4.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("DerivedB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.DerivedTypes);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_DerivedFile_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            Direction = "Both",
            DerivedFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.DerivedTypes);
        Assert.Equal("Root", Assert.Single(result.Data.BaseTypes).TypeName);
        Assert.Equal("IMarker", Assert.Single(result.Data.Interfaces).TypeName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NonexistentDerivedFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_RelativeDerivedFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = "DerivedA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NonCsDerivedFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AliasedDerivedFilePath_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "DerivedA.cs");

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.DerivedTypes.Count);
        Assert.All(result.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("DerivedA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_CaseOnlyDifferentDerivedFile_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var caseVariant = workspace.PathOf("DERIVEDA.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to DerivedA.cs. On case-sensitive volumes (Linux) derivedFile must exist to
        // pass validation (same as find_implementations implementationFile), so create a
        // type-free stub at the case-variant path; the documented OrdinalIgnoreCase comparison
        // must still match DerivedA.cs's derived types.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no derived types");

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            DerivedFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.DerivedTypes.Count);
        Assert.All(result.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("DerivedA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync(new Dictionary<string, string> { [fileName] = source }, fileName);

        public static async Task<TempWorkspace> CreateAsync(IReadOnlyDictionary<string, string> files, string? primaryFile = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetTypeHierarchy_" + Guid.NewGuid().ToString("N"));
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

            foreach (var (name, source) in files)
                await File.WriteAllTextAsync(Path.Combine(directory, name), source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var name in files.Keys)
                {
                    var filePath = Path.Combine(directory, name);
                    if (context.GetDocumentByPath(filePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {filePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = Path.Combine(directory, primaryFile ?? files.Keys.First()),
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
}

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
/// <c>find_implementations</c> <c>implementationFile</c> / <c>find_references</c> <c>referenceFile</c>),
/// and optional <c>projectPath</c> (restricts reported DerivedTypes to one project; peer of
/// <c>get_diagnostics</c> / <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c> /
/// <c>find_implementations</c> <c>projectPath</c>), and optional <c>transitive</c> (<c>false</c> reports
/// only direct subclasses instead of every descendant).
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

    #region projectPath

    // Two-project scenario: Lib/Shape.cs declares Shape (line 1, column 14) with base type Root and
    // interface IMarker; Lib/LibDerived.cs derives L1 (line 1) and Mid (line 2) from it. App (which
    // references Lib) derives P1 (line 1) and P2 (line 2) in AppA.cs, and P3 from Lib's Mid in AppB.cs
    // (line 1; transitive). Solution-wide: 5 derived types.
    private const string LibShapeSource = """
        public class Shape : Root, IMarker { }
        public class Root { }
        public interface IMarker { }
        """;

    private const string LibDerivedSource = """
        public class L1 : Shape { }
        public class Mid : Shape { }
        """;

    private const string AppASource = """
        public class P1 : Shape { }
        public class P2 : Shape { }
        """;

    private const string AppBSource = """
        public class P3 : Mid { }
        """;

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            ProjectPath = Path.Combine("Lib", "Lib.csproj")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableTheory]
    [InlineData("C:Lib.csproj")]
    [InlineData("\\Lib\\Lib.csproj")]
    public async Task Validate_ProjectPathDriveOrRootRelative_ThrowsInvalidSourcePath(string projectPath)
    {
        // Windows drive-relative / root-relative forms pass Path.IsPathRooted but are not fully
        // qualified; on non-Windows they are plain relative paths. Both must be InvalidSourcePath.
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            ProjectPath = projectPath
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            ProjectPath = workspace.PathOf("Shape.cs")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathNotInWorkspace_ThrowsEvenForAncestorsOnly()
    {
        // The scope is resolved up front, so an unknown project is reported even when the
        // Descendants search (the only part projectPath filters) is not requested.
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Direction = "Ancestors",
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathOmittedOrBlank_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape"
        });
        var blank = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = "  "
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(new[] { "L1", "Mid", "P1", "P2", "P3" }, SortedNames(omitted.Data!.DerivedTypes));
        Assert.Equal(5, omitted.Data.TotalCount);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(SortedSites(omitted.Data.DerivedTypes), SortedSites(blank.Data!.DerivedTypes));
        Assert.Equal(omitted.Data.TotalCount, blank.Data.TotalCount);
        Assert.False(blank.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // sourceFile (Lib) still locates the type; projectPath (App) only scopes reported DerivedTypes.
        // P3 derives from Lib's Mid, so it is kept even though the intermediate type is in Lib.
        var app = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal("Shape", app.Data!.TypeName);
        Assert.Equal(new[] { "P1", "P2", "P3" }, SortedNames(app.Data.DerivedTypes));
        Assert.Equal(3, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);
        Assert.All(app.Data.DerivedTypes, d => Assert.True(
            SamePath(d.File, workspace.AppAPath) || SamePath(d.File, workspace.AppBPath)));

        var lib = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(new[] { "L1", "Mid" }, SortedNames(lib.Data!.DerivedTypes));
        Assert.All(lib.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.LibDerivedPath)));
        Assert.Equal(new int?[] { 1, 2 }, lib.Data.DerivedTypes.Select(d => d.Line).OrderBy(l => l));
        Assert.Equal(2, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPath_LineColumnResolution_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            Line = 1,
            Column = 14,
            Direction = "Descendants",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(result.Success);
        Assert.Equal("Shape", result.Data!.TypeName);
        Assert.Equal(new[] { "L1", "Mid" }, SortedNames(result.Data.DerivedTypes));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPath_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root and IMarker live in Lib; scoping DerivedTypes to App must not drop them.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Direction = "Both",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "P1", "P2", "P3" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal("Root", Assert.Single(result.Data.BaseTypes).TypeName);
        Assert.Equal("IMarker", Assert.Single(result.Data.Interfaces).TypeName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathWithDerivedFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var sameProject = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            DerivedFile = workspace.AppAPath
        });

        Assert.True(sameProject.Success);
        Assert.Equal(new[] { "P1", "P2" }, SortedNames(sameProject.Data!.DerivedTypes));
        Assert.All(sameProject.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.AppAPath)));
        Assert.Equal(2, sameProject.Data.TotalCount);
        Assert.False(sameProject.Data.Truncated);

        var otherProject = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            DerivedFile = workspace.LibDerivedPath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.DerivedTypes);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathWithMaxResults_CapsProjectScopedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.StartsWith("P", d.TypeName, StringComparison.Ordinal));
        // TotalCount reflects the project-scoped set (3 in App), not the solution-wide 5.
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.DerivedTypes.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathWithDerivedFileAndMaxResults_CapsCombinedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            DerivedFile = workspace.AppAPath,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.DerivedTypes);
        Assert.True(SamePath(only.File, workspace.AppAPath));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.AppProjectPath)!, "APP.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.Equal(new[] { "P1", "P2", "P3" }, SortedNames(result.Data!.DerivedTypes));
            Assert.Equal(3, result.Data.TotalCount);
        }
        else
        {
            // Case-sensitive volume (Linux): APP.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathLinkedFile_ReportsThatProjectsDeclaration()
    {
        // Linked.cs is compiled into both Lib and App (internal, so the two copies do not clash), so
        // each project has its own Shared derived type; projectPath reports the copy declared in that
        // project's document.
        await using var workspace = await TwoProjectWorkspace.CreateAsync(linkedSource: """
            internal class Shared : Shape { }
            """);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal(new[] { "P1", "P2", "P3", "Shared" }, SortedNames(app.Data!.DerivedTypes));
        Assert.Equal(4, app.Data.TotalCount);
        Assert.True(SamePath(Assert.Single(app.Data.DerivedTypes, d => d.TypeName == "Shared").File, workspace.LinkedPath!));

        var lib = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(new[] { "L1", "Mid", "Shared" }, SortedNames(lib.Data!.DerivedTypes));
        Assert.Equal(3, lib.Data.TotalCount);
        Assert.True(SamePath(Assert.Single(lib.Data.DerivedTypes, d => d.TypeName == "Shared").File, workspace.LinkedPath!));
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape"
        });
        var scoped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Shape.cs"),
            SymbolName = "Shape",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(4, scoped.Data!.TotalCount);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(SortedSites(omitted.Data.DerivedTypes), SortedSites(scoped.Data.DerivedTypes));
    }

    #endregion

    #region transitive

    // Shape.cs: Root <- Shape (IMarker) <- {A, B} (Direct.cs) and Mid (Chain.cs) <- Leaf (Chain.cs) <- Deep (Deep.cs).
    // Direct subclasses of Shape: A, B, Mid; transitive descendants add Leaf and Deep.
    private static readonly Dictionary<string, string> ChainSources = new()
    {
        ["Shape.cs"] = """
            public class Shape : Root, IMarker { }
            public class Root { }
            public interface IMarker { }
            """,
        ["Direct.cs"] = """
            public class A : Shape { }
            public class B : Shape { }
            """,
        ["Chain.cs"] = """
            public class Mid : Shape { }
            public class Leaf : Mid { }
            """,
        ["Deep.cs"] = """
            public class Deep : Leaf { }
            """
    };

    [Fact]
    public void Transitive_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.Transitive);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveOmittedOrTrue_ReturnsEveryDescendant()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants"
        });
        var explicitTrue = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            Transitive = true
        });

        Assert.True(omitted.Success);
        Assert.True(explicitTrue.Success);
        Assert.Equal(new[] { "A", "B", "Deep", "Leaf", "Mid" }, SortedNames(omitted.Data!.DerivedTypes));
        Assert.Equal(5, omitted.Data.TotalCount);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(SortedSites(omitted.Data.DerivedTypes), SortedSites(explicitTrue.Data!.DerivedTypes));
        Assert.Equal(omitted.Data.TotalCount, explicitTrue.Data.TotalCount);
        Assert.False(explicitTrue.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalse_ReturnsOnlyDirectSubclasses()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            Transitive = false
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B", "Mid" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);

        // An intermediate type's direct children are its own, not its grandchildren.
        var mid = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Chain.cs"),
            SymbolName = "Mid",
            Direction = "Descendants",
            Transitive = false
        });

        Assert.True(mid.Success);
        var leaf = Assert.Single(mid.Data!.DerivedTypes);
        Assert.Equal("Leaf", leaf.TypeName);
        Assert.True(SamePath(leaf.File, workspace.PathOf("Chain.cs")));
        Assert.Equal(2, leaf.Line);
        Assert.Equal(1, mid.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalse_LineColumnResolution_ReturnsOnlyDirectSubclasses()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 14,
            Direction = "Descendants",
            Transitive = false
        });

        Assert.True(result.Success);
        Assert.Equal("Shape", result.Data!.TypeName);
        Assert.Equal(new[] { "A", "B", "Mid" }, SortedNames(result.Data.DerivedTypes));
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalse_GenericBase_ReturnsDirectConstructedAndOpenSubclasses()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class Box<T> { }
            public class IntBox : Box<int> { }
            public class OpenBox<T> : Box<T> { }
            public class SubIntBox : IntBox { }
            public class SubOpenBox : OpenBox<string> { }
            """);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var direct = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Box",
            Direction = "Descendants",
            Transitive = false
        });
        var all = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Box",
            Direction = "Descendants"
        });

        Assert.True(direct.Success);
        Assert.Equal(new[] { "IntBox", "OpenBox" }, SortedNames(direct.Data!.DerivedTypes));
        Assert.Equal(2, direct.Data.TotalCount);
        Assert.True(all.Success);
        Assert.Equal(new[] { "IntBox", "OpenBox", "SubIntBox", "SubOpenBox" }, SortedNames(all.Data!.DerivedTypes));
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalse_DoesNotAffectBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Deep.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var transitive = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Chain.cs"),
            SymbolName = "Leaf",
            Direction = "Both"
        });
        var direct = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.PathOf("Chain.cs"),
            SymbolName = "Leaf",
            Direction = "Both",
            Transitive = false
        });

        Assert.True(direct.Success);
        // BaseTypes is still the full ancestor chain, not just the immediate base.
        Assert.Equal(new[] { "Mid", "Shape", "Root" }, direct.Data!.BaseTypes.Select(b => b.TypeName));
        Assert.Equal("IMarker", Assert.Single(direct.Data.Interfaces).TypeName);
        Assert.Equal(
            transitive.Data!.BaseTypes.Select(b => b.FullyQualifiedName),
            direct.Data.BaseTypes.Select(b => b.FullyQualifiedName));
        Assert.Equal(
            transitive.Data.Interfaces.Select(i => i.FullyQualifiedName),
            direct.Data.Interfaces.Select(i => i.FullyQualifiedName));
        Assert.Equal("Deep", Assert.Single(direct.Data.DerivedTypes).TypeName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalse_AncestorsOnly_Unaffected()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Ancestors",
            Transitive = false
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.DerivedTypes);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.Equal("Root", Assert.Single(result.Data.BaseTypes).TypeName);
        Assert.Equal("IMarker", Assert.Single(result.Data.Interfaces).TypeName);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalseWithDerivedFile_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Chain.cs declares Mid (direct) and Leaf (grandchild): only Mid is reported.
        var chain = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Transitive = false,
            DerivedFile = workspace.PathOf("Chain.cs")
        });

        Assert.True(chain.Success);
        var mid = Assert.Single(chain.Data!.DerivedTypes);
        Assert.Equal("Mid", mid.TypeName);
        Assert.Equal(1, mid.Line);
        Assert.Equal(1, chain.Data.TotalCount);
        Assert.False(chain.Data.Truncated);

        // Deep.cs holds only a transitive descendant, so the direct set is empty there.
        var deep = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Transitive = false,
            DerivedFile = workspace.PathOf("Deep.cs")
        });

        Assert.True(deep.Success);
        Assert.Empty(deep.Data!.DerivedTypes);
        Assert.Equal(0, deep.Data.TotalCount);
        Assert.False(deep.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalseWithMaxResults_CapsDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Transitive = false,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, new[] { "A", "B", "Mid" }));
        // TotalCount reflects the direct set (3), not every descendant (5).
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Transitive = false,
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.DerivedTypes.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalseWithProjectPath_DropsTypesDerivingThroughIntermediate()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // P3 : Mid (Lib) is a transitive descendant of Shape, so the direct-only App scope drops it.
        var app = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            Transitive = false
        });

        Assert.True(app.Success);
        Assert.Equal(new[] { "P1", "P2" }, SortedNames(app.Data!.DerivedTypes));
        Assert.All(app.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.AppAPath)));
        Assert.Equal(2, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);

        var lib = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.LibProjectPath,
            Transitive = false
        });

        Assert.True(lib.Success);
        Assert.Equal(new[] { "L1", "Mid" }, SortedNames(lib.Data!.DerivedTypes));
        Assert.Equal(2, lib.Data.TotalCount);

        // Solution-wide direct set spans both projects.
        var all = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Transitive = false
        });

        Assert.True(all.Success);
        Assert.Equal(new[] { "L1", "Mid", "P1", "P2" }, SortedNames(all.Data!.DerivedTypes));
        Assert.Equal(4, all.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_TransitiveFalseWithProjectPathAndMaxResults_CapsCombinedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            Transitive = false,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.DerivedTypes);
        Assert.Contains(only.TypeName, new[] { "P1", "P2" });
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    #endregion

    private static List<string> SortedNames(IEnumerable<TypeHierarchyEntry> entries) =>
        entries.Select(e => e.TypeName).OrderBy(n => n, StringComparer.Ordinal).ToList();

    // Roslyn does not guarantee derived-type order across runs, so compare sites as sorted sets.
    private static List<(string? File, int? Line, string Name)> SortedSites(IEnumerable<TypeHierarchyEntry> entries) =>
        entries
            .Select(e => (e.File, e.Line, e.TypeName))
            .OrderBy(e => e.File, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.TypeName, StringComparer.Ordinal)
            .ToList();

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

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

    private sealed class TwoProjectWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string LibProjectPath { get; init; }
        public required string AppProjectPath { get; init; }
        public required string LibShapePath { get; init; }
        public required string LibDerivedPath { get; init; }
        public required string AppAPath { get; init; }
        public required string AppBPath { get; init; }
        public string? LinkedPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetTypeHierarchyXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libShape = Path.Combine(libDir, "Shape.cs");
            var libDerived = Path.Combine(libDir, "LibDerived.cs");
            var appA = Path.Combine(appDir, "AppA.cs");
            var appB = Path.Combine(appDir, "AppB.cs");

            // Optional source file outside both project directories, linked into both projects, so the
            // same file is a document in two projects.
            string? linkedPath = null;
            var linkedItem = linkedSource == null
                ? string.Empty
                : """<ItemGroup><Compile Include="..\Linked\Linked.cs" /></ItemGroup>""";
            if (linkedSource != null)
            {
                var linkedDir = Path.Combine(directory, "Linked");
                Directory.CreateDirectory(linkedDir);
                linkedPath = Path.Combine(linkedDir, "Linked.cs");
                await File.WriteAllTextAsync(linkedPath, linkedSource);
            }

            await File.WriteAllTextAsync(libProject, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  {linkedItem}
                </Project>
                """);
            await File.WriteAllTextAsync(appProject, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                  {linkedItem}
                </Project>
                """);
            await File.WriteAllTextAsync(libShape, LibShapeSource);
            await File.WriteAllTextAsync(libDerived, LibDerivedSource);
            await File.WriteAllTextAsync(appA, AppASource);
            await File.WriteAllTextAsync(appB, AppBSource);

            var solutionPath = Path.Combine(directory, "TestApp.sln");
            await File.WriteAllTextAsync(solutionPath, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
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
                if (context.GetDocumentByPath(libShape) == null ||
                    context.GetDocumentByPath(libDerived) == null ||
                    context.GetDocumentByPath(appA) == null ||
                    context.GetDocumentByPath(appB) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibShapePath = libShape,
                    LibDerivedPath = libDerived,
                    AppAPath = appA,
                    AppBPath = appB,
                    LinkedPath = linkedPath,
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

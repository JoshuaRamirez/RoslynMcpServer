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
/// only direct subclasses instead of every descendant), and optional <c>accessibilityFilter</c> (keeps
/// only DerivedTypes with that exact declared accessibility), and optional <c>kindFilter</c> (keeps only
/// DerivedTypes of that symbol kind).
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

    #region namespaceFilter

    // App.Core.Shape (Root, IMarker) is derived from in App.Services (S1), App.Services.Orders (S2, S3 : S1,
    // Outer.Nested), the sibling-prefix namespace App.ServicesExtra (X1), and the global namespace (G1).
    // Transitive descendants: S1, S2, S3, Nested, X1, G1; direct: all but S3.
    private static readonly Dictionary<string, string> NamespaceSources = new()
    {
        ["Shape.cs"] = """
            namespace App.Core
            {
                public class Shape : Root, IMarker { }
                public class Root { }
                public interface IMarker { }
            }
            """,
        ["Services.cs"] = """
            namespace App.Services
            {
                public class S1 : App.Core.Shape { }
            }

            namespace App.Services.Orders
            {
                public class S2 : App.Core.Shape { }
                public class S3 : App.Services.S1 { }
                public class Outer
                {
                    public class Nested : App.Core.Shape { }
                }
            }
            """,
        ["Extra.cs"] = """
            namespace App.ServicesExtra
            {
                public class X1 : App.Core.Shape { }
            }
            """,
        ["Global.cs"] = """
            public class G1 : App.Core.Shape { }
            """
    };

    private static readonly string[] AllNamespaceDescendants = { "G1", "Nested", "S1", "S2", "S3", "X1" };

    private static GetTypeHierarchyParams ShapeParams(TempWorkspace workspace, string? namespaceFilter) => new()
    {
        SourceFile = workspace.SourcePath,
        SymbolName = "Shape",
        Direction = "Descendants",
        NamespaceFilter = namespaceFilter
    };

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterOmittedBlankOrBareGlobal_ReturnsEveryNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var filter in new[] { null, "", "   ", "global::", " global:: " })
        {
            var result = await operation.ExecuteAsync(ShapeParams(workspace, filter));

            Assert.True(result.Success);
            Assert.Equal(AllNamespaceDescendants, SortedNames(result.Data!.DerivedTypes));
            Assert.Equal(6, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilter_KeepsEqualAndNestedNamespacesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // App.Services matches App.Services and App.Services.Orders, never the sibling-prefix
        // App.ServicesExtra nor the global namespace.
        var services = await operation.ExecuteAsync(ShapeParams(workspace, "App.Services"));

        Assert.True(services.Success);
        Assert.Equal(new[] { "Nested", "S1", "S2", "S3" }, SortedNames(services.Data!.DerivedTypes));
        Assert.Equal(4, services.Data.TotalCount);
        Assert.False(services.Data.Truncated);

        var orders = await operation.ExecuteAsync(ShapeParams(workspace, "App.Services.Orders"));

        Assert.True(orders.Success);
        Assert.Equal(new[] { "Nested", "S2", "S3" }, SortedNames(orders.Data!.DerivedTypes));
        Assert.Equal(3, orders.Data.TotalCount);

        var extra = await operation.ExecuteAsync(ShapeParams(workspace, "App.ServicesExtra"));

        Assert.True(extra.Success);
        Assert.Equal(new[] { "X1" }, SortedNames(extra.Data!.DerivedTypes));

        var app = await operation.ExecuteAsync(ShapeParams(workspace, "App"));

        Assert.True(app.Success);
        Assert.Equal(new[] { "Nested", "S1", "S2", "S3", "X1" }, SortedNames(app.Data!.DerivedTypes));
        Assert.Equal(5, app.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilter_LineColumnResolution_KeepsMatchingNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            Line = 3,
            Column = 18,
            Direction = "Descendants",
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(result.Success);
        Assert.Equal("Shape", result.Data!.TypeName);
        Assert.Equal(new[] { "Nested", "S2", "S3" }, SortedNames(result.Data.DerivedTypes));
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithGlobalPrefixAndWhitespace_IsNormalized()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var plain = await operation.ExecuteAsync(ShapeParams(workspace, "App.Services"));
        var prefixed = await operation.ExecuteAsync(ShapeParams(workspace, "  global::App.Services  "));

        Assert.True(prefixed.Success);
        Assert.Equal(SortedSites(plain.Data!.DerivedTypes), SortedSites(prefixed.Data!.DerivedTypes));
        Assert.Equal(plain.Data.TotalCount, prefixed.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterIsCaseSensitiveOrUnknown_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var filter in new[] { "app.services", "App.Missing", "App.Serv" })
        {
            var result = await operation.ExecuteAsync(ShapeParams(workspace, filter));

            Assert.True(result.Success);
            Assert.Empty(result.Data!.DerivedTypes);
            Assert.Equal(0, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilter_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root and IMarker live in App.Core, outside the filter, yet stay in BaseTypes / Interfaces.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            NamespaceFilter = "App.ServicesExtra"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "X1" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Contains(result.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(result.Data.Interfaces, i => i.TypeName == "IMarker");

        var ancestors = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Ancestors",
            NamespaceFilter = "App.Missing"
        });

        Assert.True(ancestors.Success);
        Assert.Empty(ancestors.Data!.DerivedTypes);
        Assert.Contains(ancestors.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(ancestors.Data.Interfaces, i => i.TypeName == "IMarker");
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            NamespaceFilter = "App.Services",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, new[] { "Nested", "S1", "S2", "S3" }));
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            NamespaceFilter = "App.Services",
            MaxResults = 4
        });

        Assert.True(exact.Success);
        Assert.Equal(4, exact.Data!.DerivedTypes.Count);
        Assert.Equal(4, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // S3 derives through S1, so the direct App.Services set drops it.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            Transitive = false,
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Nested", "S1", "S2" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithDerivedFile_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var orders = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            DerivedFile = workspace.PathOf("Services.cs"),
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(orders.Success);
        Assert.Equal(new[] { "Nested", "S2", "S3" }, SortedNames(orders.Data!.DerivedTypes));
        Assert.All(orders.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("Services.cs"))));
        Assert.Equal(3, orders.Data.TotalCount);

        // Extra.cs declares only App.ServicesExtra types, so the App.Services scope finds none there.
        var none = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            DerivedFile = workspace.PathOf("Extra.cs"),
            NamespaceFilter = "App.Services"
        });

        Assert.True(none.Success);
        Assert.Empty(none.Data!.DerivedTypes);
        Assert.Equal(0, none.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithProjectPath_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            ProjectPath = workspace.ProjectPath,
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Nested", "S1", "S2", "S3" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(4, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NamespaceFilterWithProjectPath_TwoProjects_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // TwoProjectWorkspace declares every type in the global namespace, so a named filter keeps
        // nothing in either project while a bare global:: keeps the whole project-scoped set.
        var named = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            NamespaceFilter = "App"
        });

        Assert.True(named.Success);
        Assert.Empty(named.Data!.DerivedTypes);
        Assert.Equal(0, named.Data.TotalCount);

        var bareGlobal = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath,
            NamespaceFilter = "global::"
        });
        var omitted = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(bareGlobal.Success);
        Assert.NotEmpty(omitted.Data!.DerivedTypes);
        Assert.Equal(SortedSites(omitted.Data.DerivedTypes), SortedSites(bareGlobal.Data!.DerivedTypes));
        Assert.Equal(omitted.Data.TotalCount, bareGlobal.Data.TotalCount);
    }

    #endregion

    #region accessibilityFilter

    // App.Shape (public; base Root, interface IMarker) is derived from by nested types of every
    // accessibility inside Shape.cs and by top-level public / internal (explicit and implicit) types in
    // Derived.cs; PubLeaf derives only through Pub1, so it is a transitive-only descendant.
    private static readonly Dictionary<string, string> AccessibilitySources = new()
    {
        ["Shape.cs"] = """
            namespace App
            {
                public class Root { }
                public interface IMarker { }

                public class Shape : Root, IMarker
                {
                    private class PrivNested : Shape { }
                    class DefaultNested : Shape { }
                    protected class ProtNested : Shape { }
                    protected internal class ProtIntNested : Shape { }
                    private protected class PrivProtNested : Shape { }
                    internal class IntNested : Shape { }
                }
            }
            """,
        ["Derived.cs"] = """
            namespace App
            {
                public class Pub1 : Shape { }
                public class Pub2 : Shape { }
                public class PubLeaf : Pub1 { }
                internal class Int1 : Shape { }
                class Implicit : Shape { }
            }
            """
    };

    private static readonly string[] AllAccessibilityDescendants =
    {
        "DefaultNested", "Implicit", "Int1", "IntNested", "PrivNested", "PrivProtNested",
        "ProtIntNested", "ProtNested", "Pub1", "Pub2", "PubLeaf"
    };

    private static GetTypeHierarchyParams AccessibilityParams(TempWorkspace workspace, string? accessibilityFilter) => new()
    {
        SourceFile = workspace.SourcePath,
        SymbolName = "Shape",
        Direction = "Descendants",
        AccessibilityFilter = accessibilityFilter
    };

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilterOmittedOrBlank_ReturnsEveryAccessibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var filter in new[] { null, "", "   " })
        {
            var result = await operation.ExecuteAsync(AccessibilityParams(workspace, filter));

            Assert.True(result.Success);
            Assert.Equal(AllAccessibilityDescendants, SortedNames(result.Data!.DerivedTypes));
            Assert.Equal(11, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    [SkippableTheory]
    [InlineData("public", new[] { "Pub1", "Pub2", "PubLeaf" })]
    [InlineData("internal", new[] { "Implicit", "Int1", "IntNested" })]
    [InlineData("private", new[] { "DefaultNested", "PrivNested" })]
    [InlineData("protected", new[] { "ProtNested" })]
    [InlineData("protected internal", new[] { "ProtIntNested" })]
    [InlineData("private protected", new[] { "PrivProtNested" })]
    public async Task GetTypeHierarchy_AccessibilityFilter_KeepsExactDeclaredAccessibilityOnly(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Implicit defaults count (top-level no modifier = internal, nested no modifier = private), and
        // protected internal / private protected are never matched by protected, internal, or private.
        var result = await operation.ExecuteAsync(AccessibilityParams(workspace, accessibilityFilter));

        Assert.True(result.Success);
        Assert.Equal(expected, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("PUBLIC", "public")]
    [InlineData("  Internal  ", "internal")]
    [InlineData("protected   internal", "protected internal")]
    [InlineData("internal protected", "protected internal")]
    [InlineData("Protected Private", "private protected")]
    public async Task GetTypeHierarchy_AccessibilityFilterCaseWhitespaceAndOrder_IsNormalized(
        string accessibilityFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(AccessibilityParams(workspace, accessibilityFilter));
        var expected = await operation.ExecuteAsync(AccessibilityParams(workspace, canonical));

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.DerivedTypes);
        Assert.Equal(SortedNames(expected.Data.DerivedTypes), SortedNames(filtered.Data!.DerivedTypes));
        Assert.Equal(expected.Data.TotalCount, filtered.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("friend", "Descendants")]
    [InlineData("ProtectedOrInternal", "Both")]
    [InlineData("public, internal", "Ancestors")]
    public async Task GetTypeHierarchy_UnknownAccessibilityFilter_ThrowsInvalidVisibility(
        string accessibilityFilter, string direction)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Validated up front, even when direction is Ancestors (no DerivedTypes to filter).
        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Shape",
                Direction = direction,
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.StartsWith($"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilter_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root and IMarker are public, yet stay in BaseTypes / Interfaces under a private filter.
        var both = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            AccessibilityFilter = "private"
        });

        Assert.True(both.Success);
        Assert.Equal(new[] { "DefaultNested", "PrivNested" }, SortedNames(both.Data!.DerivedTypes));
        Assert.Contains(both.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(both.Data.Interfaces, i => i.TypeName == "IMarker");

        var ancestors = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Ancestors",
            AccessibilityFilter = "private protected"
        });

        Assert.True(ancestors.Success);
        Assert.Empty(ancestors.Data!.DerivedTypes);
        Assert.Contains(ancestors.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(ancestors.Data.Interfaces, i => i.TypeName == "IMarker");
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilterWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AccessibilityFilter = "public",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, new[] { "Pub1", "Pub2", "PubLeaf" }));
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AccessibilityFilter = "public",
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.DerivedTypes.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilterWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // PubLeaf derives through Pub1, so the direct public set drops it.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            Transitive = false,
            AccessibilityFilter = "public"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Pub1", "Pub2" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilterWithDerivedFileAndProjectPath_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // IntNested is internal but declared in Shape.cs, so the Derived.cs scope drops it.
        var derivedFile = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            DerivedFile = workspace.PathOf("Derived.cs"),
            AccessibilityFilter = "internal"
        });

        Assert.True(derivedFile.Success);
        Assert.Equal(new[] { "Implicit", "Int1" }, SortedNames(derivedFile.Data!.DerivedTypes));
        Assert.All(derivedFile.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("Derived.cs"))));
        Assert.Equal(2, derivedFile.Data.TotalCount);

        // Derived.cs declares no private types, so the combined scope finds none.
        var none = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            DerivedFile = workspace.PathOf("Derived.cs"),
            AccessibilityFilter = "private"
        });

        Assert.True(none.Success);
        Assert.Empty(none.Data!.DerivedTypes);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);

        var project = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            ProjectPath = workspace.ProjectPath,
            AccessibilityFilter = "private"
        });

        Assert.True(project.Success);
        Assert.Equal(new[] { "DefaultNested", "PrivNested" }, SortedNames(project.Data!.DerivedTypes));
        Assert.Equal(2, project.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AccessibilityFilterWithNamespaceFilter_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Every NamespaceSources descendant is public, so public keeps the namespace-filtered set and
        // internal keeps nothing.
        var pub = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            NamespaceFilter = "App.Services",
            AccessibilityFilter = "public"
        });

        Assert.True(pub.Success);
        Assert.Equal(new[] { "Nested", "S1", "S2", "S3" }, SortedNames(pub.Data!.DerivedTypes));
        Assert.Equal(4, pub.Data.TotalCount);

        var internalOnly = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            NamespaceFilter = "App.Services",
            AccessibilityFilter = "internal"
        });

        Assert.True(internalOnly.Success);
        Assert.Empty(internalOnly.Data!.DerivedTypes);
        Assert.Equal(0, internalOnly.Data.TotalCount);
    }

    #endregion

    #region kindFilter

    // App.Shape (class; base Root, interface IMarker) has class descendants in Derived.cs (UnitSquare
    // only through Square); App.Entity (abstract record, interface IMarker) has record descendants in
    // Records.cs under App.Models (Employee only through Person). C# lets classes derive only from
    // classes and records only from records, so each target's derived set is all Class or all Record.
    private static readonly Dictionary<string, string> KindSources = new()
    {
        ["Shape.cs"] = """
            namespace App
            {
                public class Root { }
                public interface IMarker { }

                public class Shape : Root, IMarker { }

                public abstract record Entity : IMarker;
            }
            """,
        ["Derived.cs"] = """
            namespace App
            {
                public class Circle : Shape { }
                public class Square : Shape { }
                public class UnitSquare : Square { }
                internal class Hidden : Shape { }
            }
            """,
        ["Records.cs"] = """
            namespace App.Models
            {
                public record Person(string Name) : Entity;
                public record Employee(string Name, int Id) : Person(Name);
                internal sealed record Order : Entity;
            }
            """
    };

    private static readonly string[] AllClassDescendants = { "Circle", "Hidden", "Square", "UnitSquare" };

    private static readonly string[] AllRecordDescendants = { "Employee", "Order", "Person" };

    private static GetTypeHierarchyParams KindParams(TempWorkspace workspace, string symbolName, string? kindFilter) => new()
    {
        SourceFile = workspace.SourcePath,
        SymbolName = symbolName,
        Direction = "Descendants",
        KindFilter = kindFilter
    };

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_KindFilterOmittedOrBlank_ReturnsEveryKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var filter in new[] { null, "", "   " })
        {
            var shape = await operation.ExecuteAsync(KindParams(workspace, "Shape", filter));

            Assert.True(shape.Success);
            Assert.Equal(AllClassDescendants, SortedNames(shape.Data!.DerivedTypes));
            Assert.Equal(4, shape.Data.TotalCount);
            Assert.False(shape.Data.Truncated);

            var entity = await operation.ExecuteAsync(KindParams(workspace, "Entity", filter));

            Assert.True(entity.Success);
            Assert.Equal(AllRecordDescendants, SortedNames(entity.Data!.DerivedTypes));
            Assert.Equal(3, entity.Data.TotalCount);
        }
    }

    [SkippableTheory]
    [InlineData("Class")]
    [InlineData("class")]
    [InlineData("  CLASS  ")]
    public async Task GetTypeHierarchy_KindFilterClass_KeepsClassDescendantsOnly(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var shape = await operation.ExecuteAsync(KindParams(workspace, "Shape", kindFilter));

        Assert.True(shape.Success);
        Assert.Equal(AllClassDescendants, SortedNames(shape.Data!.DerivedTypes));
        Assert.Equal(4, shape.Data.TotalCount);
        Assert.False(shape.Data.Truncated);

        // Record descendants are Record, not Class, so a Class filter drops every one of them.
        var entity = await operation.ExecuteAsync(KindParams(workspace, "Entity", kindFilter));

        Assert.True(entity.Success);
        Assert.Empty(entity.Data!.DerivedTypes);
        Assert.Equal(0, entity.Data.TotalCount);
        Assert.False(entity.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Record")]
    [InlineData(" record ")]
    public async Task GetTypeHierarchy_KindFilterRecord_KeepsRecordDescendantsOnly(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var entity = await operation.ExecuteAsync(KindParams(workspace, "Entity", kindFilter));

        Assert.True(entity.Success);
        Assert.Equal(AllRecordDescendants, SortedNames(entity.Data!.DerivedTypes));
        Assert.Equal(3, entity.Data.TotalCount);
        Assert.False(entity.Data.Truncated);

        // The reported entry Kind is unchanged (TypeKind name), so record entries still read Class.
        Assert.All(entity.Data.DerivedTypes, d => Assert.Equal("Class", d.Kind));

        var shape = await operation.ExecuteAsync(KindParams(workspace, "Shape", kindFilter));

        Assert.True(shape.Success);
        Assert.Empty(shape.Data!.DerivedTypes);
        Assert.Equal(0, shape.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("Struct")]
    [InlineData("Interface")]
    [InlineData("Enum")]
    [InlineData("Delegate")]
    [InlineData("Method")]
    [InlineData("Local")]
    [InlineData("Parameter")]
    [InlineData("Namespace")]
    public async Task GetTypeHierarchy_KindFilterNoDerivedTypeHas_ReturnsEmptySuccess(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(KindParams(workspace, "Shape", kindFilter));

        Assert.True(result.Success);
        Assert.Empty(result.Data!.DerivedTypes);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Klass", "Descendants")]
    [InlineData("6", "Both")]
    [InlineData("Class, Record", "Ancestors")]
    public async Task GetTypeHierarchy_UnknownKindFilter_ThrowsInvalidSymbolKind(string kindFilter, string direction)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Validated up front, even when direction is Ancestors (no DerivedTypes to filter).
        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Shape",
                Direction = direction,
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_KindFilter_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root is a class and IMarker an interface, yet both stay under a Record filter.
        var both = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            KindFilter = "Record"
        });

        Assert.True(both.Success);
        Assert.Empty(both.Data!.DerivedTypes);
        Assert.Contains(both.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(both.Data.Interfaces, i => i.TypeName == "IMarker");

        var ancestors = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Ancestors",
            KindFilter = "Struct"
        });

        Assert.True(ancestors.Success);
        Assert.Empty(ancestors.Data!.DerivedTypes);
        Assert.Contains(ancestors.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(ancestors.Data.Interfaces, i => i.TypeName == "IMarker");
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_KindFilterWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Entity",
            Direction = "Descendants",
            KindFilter = "Record",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, AllRecordDescendants));
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Entity",
            Direction = "Descendants",
            KindFilter = "Record",
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.DerivedTypes.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_KindFilterWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Employee derives through Person, so the direct record set drops it.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Entity",
            Direction = "Descendants",
            Transitive = false,
            KindFilter = "Record"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Order", "Person" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_KindFilterWithOtherFilters_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var internalRecords = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Entity",
            Direction = "Descendants",
            KindFilter = "Record",
            AccessibilityFilter = "internal"
        });

        Assert.True(internalRecords.Success);
        Assert.Equal(new[] { "Order" }, SortedNames(internalRecords.Data!.DerivedTypes));
        Assert.Equal(1, internalRecords.Data.TotalCount);

        var namespaced = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Entity",
            Direction = "Descendants",
            KindFilter = "Record",
            NamespaceFilter = "App.Models",
            DerivedFile = workspace.PathOf("Records.cs"),
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(namespaced.Success);
        Assert.Equal(AllRecordDescendants, SortedNames(namespaced.Data!.DerivedTypes));
        Assert.All(namespaced.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("Records.cs"))));
        Assert.Equal(3, namespaced.Data.TotalCount);

        // Shape's class descendants live in App (Derived.cs), so App.Models + Class finds none.
        var none = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            KindFilter = "Class",
            NamespaceFilter = "App.Models"
        });

        Assert.True(none.Success);
        Assert.Empty(none.Data!.DerivedTypes);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);
    }

    #endregion

    #region nameFilter

    // App.Controller (base Root, interface IMarker) has six descendants: four whose simple names
    // contain "Admin" (SuperAdminController only through AdminController; AdminTools internal;
    // AdminReports in App.Admin, Admin.cs) and two that do not (OrderController, App.Admin.Billing).
    private static readonly Dictionary<string, string> NameSources = new()
    {
        ["Controller.cs"] = """
            namespace App
            {
                public class Root { }
                public interface IMarker { }

                public class Controller : Root, IMarker { }
            }
            """,
        ["Derived.cs"] = """
            namespace App
            {
                public class AdminController : Controller { }
                public class SuperAdminController : AdminController { }
                internal class AdminTools : Controller { }
                public class OrderController : Controller { }
            }
            """,
        ["Admin.cs"] = """
            namespace App.Admin
            {
                public class AdminReports : App.Controller { }
                public class Billing : App.Controller { }
            }
            """
    };

    private static readonly string[] AllNameDescendants =
        { "AdminController", "AdminReports", "AdminTools", "Billing", "OrderController", "SuperAdminController" };

    private static readonly string[] AdminDescendants =
        { "AdminController", "AdminReports", "AdminTools", "SuperAdminController" };

    private static GetTypeHierarchyParams NameParams(TempWorkspace workspace, string? nameFilter) => new()
    {
        SourceFile = workspace.SourcePath,
        SymbolName = "Controller",
        Direction = "Descendants",
        NameFilter = nameFilter
    };

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NameFilter);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilterOmittedOrBlank_ReturnsEveryName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var filter in new[] { null, "", "   ", "\t" })
        {
            var result = await operation.ExecuteAsync(NameParams(workspace, filter));

            Assert.True(result.Success);
            Assert.Equal(AllNameDescendants, SortedNames(result.Data!.DerivedTypes));
            Assert.Equal(6, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    [SkippableTheory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("  ADMIN  ")]
    [InlineData("dMi")]
    public async Task GetTypeHierarchy_NameFilter_KeepsCaseInsensitiveTrimmedSubstringMatchesOnly(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(NameParams(workspace, nameFilter));

        Assert.True(result.Success);
        Assert.Equal(AdminDescendants, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilter_MatchesSuffixAndInnerSubstring()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var suffix = await operation.ExecuteAsync(NameParams(workspace, "Controller"));

        Assert.True(suffix.Success);
        Assert.Equal(new[] { "AdminController", "OrderController", "SuperAdminController" }, SortedNames(suffix.Data!.DerivedTypes));
        Assert.Equal(3, suffix.Data.TotalCount);

        var inner = await operation.ExecuteAsync(NameParams(workspace, "rAdmin"));

        Assert.True(inner.Success);
        Assert.Equal(new[] { "SuperAdminController" }, SortedNames(inner.Data!.DerivedTypes));
        Assert.Equal(1, inner.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("Zebra")]
    [InlineData("Admin Controller")] // inner whitespace is not collapsed
    [InlineData("App.Admin")] // simple Name only, not the fully qualified name
    [InlineData("Controller.")]
    public async Task GetTypeHierarchy_NameFilterNoMatch_ReturnsEmptySuccess(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(NameParams(workspace, nameFilter));

        Assert.True(result.Success);
        Assert.Empty(result.Data!.DerivedTypes);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilter_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root and IMarker do not contain "Admin", yet both stay in BaseTypes / Interfaces.
        var both = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            NameFilter = "Admin"
        });

        Assert.True(both.Success);
        Assert.Equal(AdminDescendants, SortedNames(both.Data!.DerivedTypes));
        Assert.Contains(both.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(both.Data.Interfaces, i => i.TypeName == "IMarker");

        // A filter that only matches a base type / interface name does not pull them into DerivedTypes.
        var ancestors = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Ancestors",
            NameFilter = "Zebra"
        });

        Assert.True(ancestors.Success);
        Assert.Empty(ancestors.Data!.DerivedTypes);
        Assert.Contains(ancestors.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(ancestors.Data.Interfaces, i => i.TypeName == "IMarker");

        var rootOnly = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            NameFilter = "Root"
        });

        Assert.True(rootOnly.Success);
        Assert.Empty(rootOnly.Data!.DerivedTypes);
        Assert.Equal(0, rootOnly.Data.TotalCount);
        Assert.Contains(rootOnly.Data.BaseTypes, b => b.TypeName == "Root");
        Assert.Contains(rootOnly.Data.Interfaces, i => i.TypeName == "IMarker");
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilterWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "admin",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, AdminDescendants));
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "admin",
            MaxResults = 4
        });

        Assert.True(exact.Success);
        Assert.Equal(AdminDescendants, SortedNames(exact.Data!.DerivedTypes));
        Assert.Equal(4, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilterWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // SuperAdminController derives through AdminController, so the direct set drops it.
        var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            Transitive = false,
            NameFilter = "Admin"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "AdminController", "AdminReports", "AdminTools" }, SortedNames(result.Data!.DerivedTypes));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_NameFilterWithOtherFilters_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources, "Controller.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var internalAdmin = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "Admin",
            AccessibilityFilter = "internal"
        });

        Assert.True(internalAdmin.Success);
        Assert.Equal(new[] { "AdminTools" }, SortedNames(internalAdmin.Data!.DerivedTypes));
        Assert.Equal(1, internalAdmin.Data.TotalCount);

        var namespaced = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "Admin",
            NamespaceFilter = "App.Admin"
        });

        Assert.True(namespaced.Success);
        Assert.Equal(new[] { "AdminReports" }, SortedNames(namespaced.Data!.DerivedTypes));
        Assert.Equal(1, namespaced.Data.TotalCount);

        var classes = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "Admin",
            KindFilter = "Class"
        });

        Assert.True(classes.Success);
        Assert.Equal(AdminDescendants, SortedNames(classes.Data!.DerivedTypes));
        Assert.Equal(4, classes.Data.TotalCount);

        var records = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "Admin",
            KindFilter = "Record"
        });

        Assert.True(records.Success);
        Assert.Empty(records.Data!.DerivedTypes);
        Assert.Equal(0, records.Data.TotalCount);

        var scoped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "admin",
            DerivedFile = workspace.PathOf("Derived.cs"),
            ProjectPath = workspace.ProjectPath,
            MaxResults = 2
        });

        Assert.True(scoped.Success);
        Assert.Equal(2, scoped.Data!.DerivedTypes.Count);
        Assert.All(scoped.Data.DerivedTypes, d =>
        {
            Assert.Contains(d.TypeName, new[] { "AdminController", "AdminTools", "SuperAdminController" });
            Assert.True(SamePath(d.File, workspace.PathOf("Derived.cs")));
        });
        Assert.Equal(3, scoped.Data.TotalCount);
        Assert.True(scoped.Data.Truncated);

        // OrderController lives in App and neither App.Admin type contains "Order", so a public +
        // App.Admin + "Order" filter finds none.
        var none = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Controller",
            Direction = "Descendants",
            NameFilter = "Order",
            NamespaceFilter = "App.Admin",
            AccessibilityFilter = "public"
        });

        Assert.True(none.Success);
        Assert.Empty(none.Data!.DerivedTypes);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);
    }

    #endregion

    #region abstractFilter (#3124)

    // Abstract App.Shape (base Root, interface IMarker) has seven descendants: abstract intermediates
    // Polygon and Curve (a partial type where only the Derived.cs part says abstract), concrete Circle
    // (through Curve), concrete Square and sealed Triangle (through Polygon), concrete Line (direct),
    // and internal abstract Blob (direct, App.Other in Other.cs).
    private static readonly Dictionary<string, string> AbstractSources = new()
    {
        ["Shape.cs"] = """
            namespace App
            {
                public class Root { }
                public interface IMarker { }

                public abstract class Shape : Root, IMarker { }
            }
            """,
        ["Derived.cs"] = """
            namespace App
            {
                public abstract class Polygon : Shape { }
                public class Square : Polygon { }
                public sealed class Triangle : Polygon { }
                public class Line : Shape { }
                public abstract partial class Curve : Shape { }
                public class Circle : Curve { }
            }
            """,
        ["Other.cs"] = """
            namespace App.Other
            {
                internal abstract class Blob : App.Shape { }
            }

            namespace App
            {
                public partial class Curve { }
            }
            """
    };

    private static readonly string[] AllAbstractDescendants =
        { "Blob", "Circle", "Curve", "Line", "Polygon", "Square", "Triangle" };

    private static readonly string[] AbstractDescendants = { "Blob", "Curve", "Polygon" };

    private static readonly string[] ConcreteDescendants = { "Circle", "Line", "Square", "Triangle" };

    private static GetTypeHierarchyParams AbstractParams(TempWorkspace workspace, bool? abstractFilter) => new()
    {
        SourceFile = workspace.SourcePath,
        SymbolName = "Shape",
        Direction = "Descendants",
        AbstractFilter = abstractFilter
    };

    [Fact]
    public void AbstractFilter_DefaultsToNull()
    {
        var @params = new GetTypeHierarchyParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.AbstractFilter);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_SplitsAbstractAndConcreteDescendants()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var all = await operation.ExecuteAsync(AbstractParams(workspace, null));
        Assert.True(all.Success);
        Assert.Equal(AllAbstractDescendants, SortedNames(all.Data!.DerivedTypes));
        Assert.Equal(7, all.Data.TotalCount);
        Assert.False(all.Data.Truncated);

        // Curve is abstract although only one of its partial declarations says so.
        var abstracts = await operation.ExecuteAsync(AbstractParams(workspace, true));
        Assert.True(abstracts.Success);
        Assert.Equal(AbstractDescendants, SortedNames(abstracts.Data!.DerivedTypes));
        Assert.Equal(3, abstracts.Data.TotalCount);
        Assert.False(abstracts.Data.Truncated);

        // Sealed Triangle is concrete.
        var concrete = await operation.ExecuteAsync(AbstractParams(workspace, false));
        Assert.True(concrete.Success);
        Assert.Equal(ConcreteDescendants, SortedNames(concrete.Data!.DerivedTypes));
        Assert.Equal(4, concrete.Data.TotalCount);
        Assert.False(concrete.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_JudgesRecords()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App
            {
                public abstract record Event;
                public abstract record DomainEvent : Event;
                public record Created : DomainEvent;
                public sealed record Deleted : Event;
            }
            """, "Events.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        GetTypeHierarchyParams Params(bool? filter) => new()
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Event",
            Direction = "Descendants",
            AbstractFilter = filter
        };

        var abstracts = await operation.ExecuteAsync(Params(true));
        Assert.True(abstracts.Success);
        Assert.Equal(new[] { "DomainEvent" }, SortedNames(abstracts.Data!.DerivedTypes));
        Assert.Equal(1, abstracts.Data.TotalCount);

        var concrete = await operation.ExecuteAsync(Params(false));
        Assert.True(concrete.Success);
        Assert.Equal(new[] { "Created", "Deleted" }, SortedNames(concrete.Data!.DerivedTypes));
        Assert.Equal(2, concrete.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilterWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        GetTypeHierarchyParams Params(bool filter) => new()
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            Transitive = false,
            AbstractFilter = filter
        };

        // Square / Triangle / Circle derive through abstract intermediates, so the direct concrete
        // set is only Line.
        var concrete = await operation.ExecuteAsync(Params(false));
        Assert.True(concrete.Success);
        Assert.Equal(new[] { "Line" }, SortedNames(concrete.Data!.DerivedTypes));
        Assert.Equal(1, concrete.Data.TotalCount);
        Assert.False(concrete.Data.Truncated);

        var abstracts = await operation.ExecuteAsync(Params(true));
        Assert.True(abstracts.Success);
        Assert.Equal(AbstractDescendants, SortedNames(abstracts.Data!.DerivedTypes));
        Assert.Equal(3, abstracts.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilterWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = false,
            MaxResults = 3
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.DerivedTypes.Count);
        Assert.All(capped.Data.DerivedTypes, d => Assert.Contains(d.TypeName, ConcreteDescendants));
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = true,
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(AbstractDescendants, SortedNames(exact.Data!.DerivedTypes));
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilterWithOtherFilters_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        var internalAbstract = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = true,
            AccessibilityFilter = "internal"
        });
        Assert.True(internalAbstract.Success);
        Assert.Equal(new[] { "Blob" }, SortedNames(internalAbstract.Data!.DerivedTypes));

        var namespaced = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = false,
            NamespaceFilter = "App.Other"
        });
        Assert.True(namespaced.Success);
        Assert.Empty(namespaced.Data!.DerivedTypes);
        Assert.Equal(0, namespaced.Data.TotalCount);
        Assert.False(namespaced.Data.Truncated);

        var named = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = false,
            NameFilter = "l"
        });
        Assert.True(named.Success);
        Assert.Equal(new[] { "Circle", "Line", "Triangle" }, SortedNames(named.Data!.DerivedTypes));

        var records = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = true,
            KindFilter = "Record"
        });
        Assert.True(records.Success);
        Assert.Empty(records.Data!.DerivedTypes);

        // Curve's abstract part is in Derived.cs, so a Derived.cs + project scope reports it there;
        // Blob lives in Other.cs and is dropped.
        var scoped = await operation.ExecuteAsync(new GetTypeHierarchyParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = true,
            DerivedFile = workspace.PathOf("Derived.cs"),
            ProjectPath = workspace.ProjectPath
        });
        Assert.True(scoped.Success);
        Assert.Equal(new[] { "Curve", "Polygon" }, SortedNames(scoped.Data!.DerivedTypes));
        Assert.All(scoped.Data.DerivedTypes, d => Assert.True(SamePath(d.File, workspace.PathOf("Derived.cs"))));
        Assert.Equal(2, scoped.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_DoesNotFilterBaseTypesOrInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractSources, "Shape.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        // Root is concrete and IMarker is an (abstract) interface, yet both stay for either value.
        foreach (var filter in new[] { true, false })
        {
            var both = await operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Shape",
                AbstractFilter = filter
            });
            Assert.True(both.Success);
            Assert.Equal(filter ? AbstractDescendants : ConcreteDescendants, SortedNames(both.Data!.DerivedTypes));
            Assert.Contains(both.Data.BaseTypes, b => b.TypeName == "Root");
            Assert.Contains(both.Data.Interfaces, i => i.TypeName == "IMarker");

            var ancestors = await operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Shape",
                Direction = "Ancestors",
                AbstractFilter = filter
            });
            Assert.True(ancestors.Success);
            Assert.Empty(ancestors.Data!.DerivedTypes);
            Assert.Equal(0, ancestors.Data.TotalCount);
            Assert.Contains(ancestors.Data.BaseTypes, b => b.TypeName == "Root");
            Assert.Contains(ancestors.Data.Interfaces, i => i.TypeName == "IMarker");
        }
    }

    // Edge roots for abstractFilter. Each row is a root and the sorted DerivedTypes for null / true /
    // false ("" = empty). Interfaces (implementers and derived interfaces are never listed; use
    // find_implementations), structs, record structs, enums, delegates, static and sealed classes always
    // return empty DerivedTypes. The root itself is always reported and never filtered (abstract AbsRoot
    // with true gives an empty list). Transitive search does not prune: concrete Leaf under abstract
    // AbsMid stays listed for false. Subclasses of a constructed generic base (IntD : Base<int>) and
    // nested subclasses are judged on their own modifier, and an abstract class with no abstract members
    // (NoAbstractMembers) counts as abstract.
    private const string AbstractEdgeSource = """
        namespace P
        {
            public interface IFoo { }
            public class FooImpl : IFoo { }
            public abstract class AbsFooImpl : IFoo { }
            public interface IBar : IFoo { }
            public struct S { }
            public record struct RS;
            public enum E { A }
            public delegate void D();
            public static class St { }
            public sealed class Se { }
            public abstract class AbsRoot { }
            public class ConcreteUnderAbsRoot : AbsRoot { }
            public class Root { }
            public abstract class AbsMid : Root { }
            public class Leaf : AbsMid { }
            public abstract class MyAbsEx : System.Exception { }
            public class MyEx : System.Exception { }
            public abstract class Base<T> { }
            public class IntD : Base<int> { }
            public abstract class AbsStrD : Base<string> { }
            public class GenD<T> : Base<T> { }
            public abstract class NoAbstractMembers : Root { public void M() { } }
            public class Outer : Root
            {
                public abstract class NestedAbs : Root { }
                public class NestedConcrete : NestedAbs { }
            }
        }
        """;


    [SkippableTheory]
    [InlineData("IFoo", "Interface", "", "", "")]
    [InlineData("IBar", "Interface", "", "", "")]
    [InlineData("S", "Struct", "", "", "")]
    [InlineData("RS", "Struct", "", "", "")]
    [InlineData("E", "Enum", "", "", "")]
    [InlineData("D", "Delegate", "", "", "")]
    [InlineData("St", "Class", "", "", "")]
    [InlineData("Se", "Class", "", "", "")]
    [InlineData("AbsRoot", "Class", "ConcreteUnderAbsRoot", "", "ConcreteUnderAbsRoot")]
    [InlineData("AbsMid", "Class", "Leaf", "", "Leaf")]
    [InlineData("Root", "Class", "AbsMid,Leaf,NestedAbs,NestedConcrete,NoAbstractMembers,Outer",
        "AbsMid,NestedAbs,NoAbstractMembers", "Leaf,NestedConcrete,Outer")]
    [InlineData("Base", "Class", "AbsStrD,GenD,IntD", "AbsStrD", "GenD,IntD")]
    public async Task GetTypeHierarchy_AbstractFilter_EdgeRoots(
        string root, string kind, string expectedAll, string expectedAbstract, string expectedConcrete)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEdgeSource, "Edge.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        foreach (var (filter, expected) in new (bool?, string)[]
                 { (null, expectedAll), (true, expectedAbstract), (false, expectedConcrete) })
        {
            var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = root,
                Direction = "Descendants",
                AbstractFilter = filter
            });

            Assert.True(result.Success);
            Assert.Equal(root, result.Data!.TypeName);
            Assert.Equal(kind, result.Data.Kind);
            var names = expected.Length == 0 ? new List<string>() : expected.Split(',').ToList();
            Assert.Equal(names, SortedNames(result.Data.DerivedTypes));
            Assert.Equal(names.Count, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    // Targeting the constructed Base<int> (at IntD's base list) resolves to Base<int> but reports the
    // subclasses of every construction of Base<T>, each judged on its own modifier.
    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_ConstructedGenericTarget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEdgeSource, "Edge.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var (line, column) = PositionOf(AbstractEdgeSource, "class IntD : Base<int>", "Base");

        foreach (var (filter, expected) in new (bool?, string[])[]
                 {
                     (null, new[] { "AbsStrD", "GenD", "IntD" }),
                     (true, new[] { "AbsStrD" }),
                     (false, new[] { "GenD", "IntD" })
                 })
        {
            var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Base",
                Line = line,
                Column = column,
                Direction = "Descendants",
                AbstractFilter = filter
            });

            Assert.True(result.Success);
            Assert.Equal("P.Base<int>", result.Data!.FullyQualifiedName);
            Assert.Equal(expected, SortedNames(result.Data.DerivedTypes));
        }
    }

    // Types from compiled libraries are judged too: System.Exception's abstract descendants are the
    // metadata DbException and the source MyAbsEx; true and false partition the full list. With transitive
    // false only the direct abstract subclass MyAbsEx remains.
    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_JudgesLibraryTypes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractEdgeSource, "Edge.cs");
        var operation = new GetTypeHierarchyOperation(workspace.Context);
        var (line, column) = PositionOf(AbstractEdgeSource, "class MyAbsEx : System.Exception", "Exception");

        async Task<GetTypeHierarchyResult> Run(bool? filter, bool? transitive = null)
        {
            var result = await operation.ExecuteAsync(new GetTypeHierarchyParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Exception",
                Line = line,
                Column = column,
                Direction = "Descendants",
                Transitive = transitive,
                AbstractFilter = filter
            });
            Assert.True(result.Success);
            Assert.Equal("System.Exception", result.Data!.FullyQualifiedName);
            return result.Data;
        }

        var all = await Run(null);
        var abstracts = await Run(true);
        var concrete = await Run(false);

        Assert.Contains(abstracts.DerivedTypes, d => d.TypeName == "MyAbsEx" && d.File != null);
        Assert.Contains(abstracts.DerivedTypes, d => d.TypeName == "DbException" && d.File == null);
        Assert.DoesNotContain(abstracts.DerivedTypes, d => d.TypeName is "MyEx" or "ArgumentException");
        Assert.Contains(concrete.DerivedTypes, d => d.TypeName == "MyEx");
        Assert.Contains(concrete.DerivedTypes, d => d.TypeName == "ArgumentException" && d.File == null);
        Assert.DoesNotContain(concrete.DerivedTypes, d => d.TypeName is "MyAbsEx" or "DbException");
        Assert.Equal(all.TotalCount, abstracts.TotalCount + concrete.TotalCount);
        Assert.Equal(
            SortedNames(all.DerivedTypes),
            SortedNames(abstracts.DerivedTypes.Concat(concrete.DerivedTypes)));

        var directAbstract = await Run(true, transitive: false);
        Assert.Equal(new[] { "MyAbsEx" }, SortedNames(directAbstract.DerivedTypes));
    }

    // Derived types in another project are judged too: App's abstract AppAbs and concrete AppConcrete
    // derive from Lib's concrete Shape alongside Lib's L1 / Mid and App's P1 / P2 / P3.
    [SkippableFact]
    public async Task GetTypeHierarchy_AbstractFilter_JudgesDerivedTypesInAnotherProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(appExtraSource: """
            public abstract class AppAbs : Shape { }
            public class AppConcrete : AppAbs { }
            """);
        var operation = new GetTypeHierarchyOperation(workspace.Context);

        GetTypeHierarchyParams Params(bool? filter) => new()
        {
            SourceFile = workspace.LibShapePath,
            SymbolName = "Shape",
            Direction = "Descendants",
            AbstractFilter = filter
        };

        var abstracts = await operation.ExecuteAsync(Params(true));
        Assert.True(abstracts.Success);
        Assert.Equal(new[] { "AppAbs" }, SortedNames(abstracts.Data!.DerivedTypes));

        var concrete = await operation.ExecuteAsync(Params(false));
        Assert.True(concrete.Success);
        Assert.Equal(
            new[] { "AppConcrete", "L1", "Mid", "P1", "P2", "P3" },
            SortedNames(concrete.Data!.DerivedTypes));
    }

    private static (int Line, int Column) PositionOf(string source, string lineText, string token)
    {
        var lines = source.Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(lineText, StringComparison.Ordinal));
        Assert.True(index >= 0, lineText);
        var start = lines[index].IndexOf(lineText, StringComparison.Ordinal);
        return (index + 1, lines[index].IndexOf(token, start, StringComparison.Ordinal) + 1);
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

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null, string? appExtraSource = null)
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
            if (appExtraSource != null)
                await File.WriteAllTextAsync(Path.Combine(appDir, "AppExtra.cs"), appExtraSource);

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

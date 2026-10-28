using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindImplementationsOperation"/> optional
/// <c>implementationFile</c> (restricts reported implementations to one file; peer of
/// <c>find_references</c> <c>referenceFile</c> / <c>find_callers</c> <c>callerFile</c>), and optional
/// <c>projectPath</c> (restricts reported implementations to one project; peer of
/// <c>get_diagnostics</c> / <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c>
/// <c>projectPath</c>), and optional <c>transitive</c> (direct interface implementers only when
/// false; peer of <c>get_type_hierarchy</c> <c>transitive</c>), and optional <c>namespaceFilter</c>
/// (restricts reported implementations to one namespace subtree; peer of <c>search_symbols</c>
/// <c>namespaceFilter</c>), and optional <c>accessibilityFilter</c> (restricts reported
/// implementations by declared accessibility; peer of <c>search_symbols</c>
/// <c>accessibilityFilter</c>), and optional <c>kindFilter</c> (restricts reported implementations
/// by reported kind; peer of <c>search_symbols</c> / <c>get_symbol_info</c> <c>kindFilter</c>), and
/// optional <c>nameFilter</c> (restricts reported implementations by name substring; peer of
/// <c>get_document_outline</c> / <c>get_symbol_info</c> <c>nameFilter</c>), and optional
/// <c>abstractFilter</c> (restricts reported implementations by <c>IsAbstract</c>; peer of
/// <c>get_type_hierarchy</c> <c>abstractFilter</c>).
/// </summary>
public class FindImplementationsOperationTests
{
    // IRunner is declared in IRunner.cs (line 1, column 18). It is implemented twice in ImplA.cs
    // (A1, A2), once in ImplB.cs (B1), and once by partial class Split whose parts live in
    // SplitOne.cs (declares the interface) and SplitTwo.cs (declares Run). Empty.cs has none.
    private static readonly Dictionary<string, string> MultiFileSources = new()
    {
        ["IRunner.cs"] = """
            public interface IRunner { void Run(); }
            """,
        ["ImplA.cs"] = """
            public class A1 : IRunner { public void Run() { } }
            public class A2 : IRunner { public void Run() { } }
            """,
        ["ImplB.cs"] = """
            public class B1 : IRunner { public void Run() { } }
            """,
        ["SplitOne.cs"] = """
            public partial class Split : IRunner { }
            """,
        ["SplitTwo.cs"] = """
            // second part of Split
            public partial class Split { public void Run() { } }
            """,
        ["Empty.cs"] = """
            public class Unrelated
            {
                public void Nothing() { }
            }
            """
    };

    [Fact]
    public void ImplementationFile_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.ImplementationFile);
    }

    [SkippableFact]
    public async Task FindImplementations_OmittedImplementationFile_ReturnsImplementationsFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2", "B1", "Split" }, result.Data.Implementations.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal(2, result.Data.Implementations.Count(i => SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(1, result.Data.Implementations.Count(i => SamePath(i.File, workspace.PathOf("ImplB.cs"))));
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "A1", "A2" }, result.Data.Implementations.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(new[] { 1, 2 }, result.Data.Implementations.Select(i => i.Line).OrderBy(l => l));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("IRunner", result.Data.SymbolName);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFile_LineColumnResolution_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            Line = 1,
            Column = 18,
            ImplementationFile = workspace.PathOf("ImplB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.Equal("B1", only.Name);
        Assert.True(SamePath(only.File, workspace.PathOf("ImplB.cs")));
        Assert.Equal(1, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileIsSecondaryPartialPart_ReportsThatPart()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("SplitTwo.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.Equal("Split", only.Name);
        // Reported at the part declared in implementationFile, not the first source location.
        Assert.True(SamePath(only.File, workspace.PathOf("SplitTwo.cs")));
        Assert.Equal(2, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithNoImplementations_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Implementations);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Implementations);
        Assert.True(SamePath(only.File, workspace.PathOf("ImplA.cs")));
        // TotalCount reflects the filtered set (2 in ImplA.cs), not the solution-wide 4.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ImplementationFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ImplB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Implementations);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NonexistentImplementationFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_RelativeImplementationFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = "ImplA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_NonCsImplementationFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindImplementations_AliasedImplementationFilePath_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "ImplA.cs");

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Implementations.Count);
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_CaseOnlyDifferentImplementationFile_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);
        var caseVariant = workspace.PathOf("IMPLA.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to ImplA.cs. On case-sensitive volumes (Linux) implementationFile must exist to
        // pass validation (same as find_references referenceFile), so create an
        // implementation-free stub at the case-variant path; the documented OrdinalIgnoreCase
        // comparison must still match ImplA.cs's implementations.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no implementations");

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Implementations.Count);
        Assert.All(result.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("ImplA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #region projectPath

    // Two-project scenario: Lib/IRunner.cs declares IRunner (line 1, column 18) and Lib/LibImpl.cs
    // implements it once (L1, line 1); App (which references Lib) implements it twice in AppA.cs
    // (P1 line 1, P2 line 2) and once in AppB.cs (P3 line 1). Solution-wide: 4 implementations.
    private const string LibInterfaceSource = """
        public interface IRunner { void Run(); }
        """;

    private const string LibImplSource = """
        public class L1 : IRunner { public void Run() { } }
        """;

    private const string AppASource = """
        public class P1 : IRunner { public void Run() { } }
        public class P2 : IRunner { public void Run() { } }
        """;

    private const string AppBSource = """
        public class P3 : IRunner { public void Run() { } }
        """;

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
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
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ProjectPath = projectPath
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ProjectPath = workspace.PathOf("IRunner.cs")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathOmittedOrBlank_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner"
        });
        var blank = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = "  "
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(new[] { "L1", "P1", "P2", "P3" }, SortedNames(omitted.Data!.Implementations));
        Assert.Equal(4, omitted.Data.TotalCount);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(SortedSites(omitted.Data.Implementations), SortedSites(blank.Data!.Implementations));
        Assert.Equal(omitted.Data.TotalCount, blank.Data.TotalCount);
        Assert.False(blank.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        // sourceFile (Lib) still locates the symbol; projectPath (App) only scopes reported implementations.
        var app = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal("IRunner", app.Data!.SymbolName);
        Assert.Equal(new[] { "P1", "P2", "P3" }, SortedNames(app.Data.Implementations));
        Assert.Equal(3, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);
        Assert.All(app.Data.Implementations, i => Assert.True(
            SamePath(i.File, workspace.AppAPath) || SamePath(i.File, workspace.AppBPath)));

        var lib = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        var only = Assert.Single(lib.Data!.Implementations);
        Assert.Equal("L1", only.Name);
        Assert.True(SamePath(only.File, workspace.LibImplPath));
        Assert.Equal(1, only.Line);
        Assert.Equal(1, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPath_LineColumnResolution_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            Line = 1,
            Column = 18,
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Implementations);
        Assert.Equal("L1", only.Name);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathWithImplementationFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var sameProject = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            ImplementationFile = workspace.AppAPath
        });

        Assert.True(sameProject.Success);
        Assert.Equal(new[] { "P1", "P2" }, SortedNames(sameProject.Data!.Implementations));
        Assert.All(sameProject.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.AppAPath)));
        Assert.Equal(2, sameProject.Data.TotalCount);
        Assert.False(sameProject.Data.Truncated);

        var otherProject = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            ImplementationFile = workspace.LibImplPath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.Implementations);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathWithMaxResults_CapsProjectScopedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.Implementations.Count);
        Assert.All(capped.Data.Implementations, i => Assert.StartsWith("P", i.Name, StringComparison.Ordinal));
        // TotalCount reflects the project-scoped set (3 in App), not the solution-wide 4.
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.Implementations.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathWithImplementationFileAndMaxResults_CapsCombinedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            ImplementationFile = workspace.AppAPath,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Implementations);
        Assert.True(SamePath(only.File, workspace.AppAPath));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.AppProjectPath)!, "APP.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.Equal(new[] { "P1", "P2", "P3" }, SortedNames(result.Data!.Implementations));
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
    public async Task FindImplementations_ProjectPathLinkedFile_ReportsThatProjectsDeclaration()
    {
        // Linked.cs is compiled into both Lib and App (internal, so the two copies do not clash), so
        // each project has its own Shared implementation; projectPath reports the copy declared in
        // that project's document.
        await using var workspace = await TwoProjectWorkspace.CreateAsync(linkedSource: """
            internal class Shared : IRunner { public void Run() { } }
            """);
        var operation = new FindImplementationsOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal(new[] { "P1", "P2", "P3", "Shared" }, SortedNames(app.Data!.Implementations));
        Assert.Equal(4, app.Data.TotalCount);
        Assert.True(SamePath(Assert.Single(app.Data.Implementations, i => i.Name == "Shared").File, workspace.LinkedPath!));

        var lib = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(new[] { "L1", "Shared" }, SortedNames(lib.Data!.Implementations));
        Assert.Equal(2, lib.Data.TotalCount);
        Assert.True(SamePath(Assert.Single(lib.Data.Implementations, i => i.Name == "Shared").File, workspace.LinkedPath!));
    }

    [SkippableFact]
    public async Task FindImplementations_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner"
        });
        var scoped = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(4, scoped.Data!.TotalCount);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(SortedSites(omitted.Data.Implementations), SortedSites(scoped.Data.Implementations));
    }

    #endregion

    #region transitive

    // Interface chain: IRunner.cs declares IRunner (line 1, column 18) and IFastRunner : IRunner
    // (line 2). Direct.cs lists IRunner directly on A (line 1), struct S (line 2) and abstract Base
    // (line 3). Inherited.cs has types that only inherit the implementation from a base class (Sub : A
    // line 1, SubSub : Sub line 2, Concrete : Base line 4) plus Relisted : A, IRunner (line 3), which
    // re-lists the interface itself. ViaDerived.cs has Turbo : IFastRunner (line 1), implementing
    // IRunner only through a derived interface. Transitive: 8 implementations; direct: A, Base,
    // Relisted, S.
    private static readonly Dictionary<string, string> ChainSources = new()
    {
        ["IRunner.cs"] = """
            public interface IRunner { void Run(); }
            public interface IFastRunner : IRunner { }
            """,
        ["Direct.cs"] = """
            public class A : IRunner { public virtual void Run() { } }
            public struct S : IRunner { public void Run() { } }
            public abstract class Base : IRunner { public abstract void Run(); }
            """,
        ["Inherited.cs"] = """
            public class Sub : A { }
            public class SubSub : Sub { }
            public class Relisted : A, IRunner { }
            public class Concrete : Base { public override void Run() { } }
            """,
        ["ViaDerived.cs"] = """
            public class Turbo : IFastRunner { public void Run() { } }
            """,
        ["Generic.cs"] = """
            public interface IBox<T> { T Get(); }
            public class IntBox : IBox<int> { public int Get() => 0; }
            public class OpenBox<T> : IBox<T> { public T Get() => default!; }
            public class SubIntBox : IntBox { }
            public class SubOpenBox<T> : OpenBox<T> { }
            """
    };

    private static readonly string[] AllChainImplementations =
        ["A", "Base", "Concrete", "Relisted", "S", "Sub", "SubSub", "Turbo"];

    private static readonly string[] DirectChainImplementations = ["A", "Base", "Relisted", "S"];

    [Fact]
    public void Transitive_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.Transitive);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveOmittedOrTrue_ReturnsEveryImplementation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner"
        });
        var transitive = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            Transitive = true
        });

        Assert.True(omitted.Success);
        Assert.True(transitive.Success);
        Assert.Equal(AllChainImplementations, SortedNames(omitted.Data!.Implementations));
        Assert.Equal(8, omitted.Data.TotalCount);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(SortedSites(omitted.Data.Implementations), SortedSites(transitive.Data!.Implementations));
        Assert.Equal(omitted.Data.TotalCount, transitive.Data.TotalCount);
        Assert.False(transitive.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_ReturnsOnlyDirectImplementers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            Transitive = false
        });

        Assert.True(result.Success);
        Assert.Equal("IRunner", result.Data!.SymbolName);
        // Sub / SubSub / Concrete only inherit the implementation and Turbo only implements it via
        // IFastRunner, so they are dropped; Relisted re-lists IRunner itself, so it stays.
        Assert.Equal(DirectChainImplementations, SortedNames(result.Data.Implementations));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);

        var relisted = Assert.Single(result.Data.Implementations, i => i.Name == "Relisted");
        Assert.True(SamePath(relisted.File, workspace.PathOf("Inherited.cs")));
        Assert.Equal(3, relisted.Line);
        Assert.All(
            result.Data.Implementations.Where(i => i.Name != "Relisted"),
            i => Assert.True(SamePath(i.File, workspace.PathOf("Direct.cs"))));
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_DerivedInterfaceTarget_ReturnsItsDirectImplementers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IFastRunner",
            Transitive = false
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Implementations);
        Assert.Equal("Turbo", only.Name);
        Assert.True(SamePath(only.File, workspace.PathOf("ViaDerived.cs")));
        Assert.Equal(1, only.Line);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_LineColumnResolution_ReturnsOnlyDirectImplementers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            Line = 1,
            Column = 18,
            Transitive = false
        });

        Assert.True(result.Success);
        Assert.Equal("IRunner", result.Data!.SymbolName);
        Assert.Equal(DirectChainImplementations, SortedNames(result.Data.Implementations));
        Assert.Equal(4, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_GenericInterface_ReturnsConstructedAndOpenDirectImplementers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var direct = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Generic.cs"),
            SymbolName = "IBox",
            Transitive = false
        });
        var transitive = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Generic.cs"),
            SymbolName = "IBox"
        });

        Assert.True(direct.Success);
        Assert.Equal(new[] { "IntBox", "OpenBox" }, SortedNames(direct.Data!.Implementations));
        Assert.Equal(2, direct.Data.TotalCount);
        Assert.True(transitive.Success);
        Assert.Equal(new[] { "IntBox", "OpenBox", "SubIntBox", "SubOpenBox" }, SortedNames(transitive.Data!.Implementations));
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_MemberTarget_Unaffected()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        // IRunner.Run (line 1, column 33): transitive only applies to interface types.
        var omitted = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            Line = 1,
            Column = 33
        });
        var direct = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            Line = 1,
            Column = 33,
            Transitive = false
        });

        Assert.True(omitted.Success);
        Assert.True(direct.Success);
        Assert.Equal("Run", omitted.Data!.SymbolName);
        Assert.NotEmpty(omitted.Data.Implementations);
        Assert.Equal(SortedSites(omitted.Data.Implementations), SortedSites(direct.Data!.Implementations));
        Assert.Equal(omitted.Data.TotalCount, direct.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalse_NonInterfaceType_Unaffected()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Direct.cs"),
            SymbolName = "Base"
        });
        var direct = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Direct.cs"),
            SymbolName = "Base",
            Transitive = false
        });

        Assert.True(omitted.Success);
        Assert.True(direct.Success);
        Assert.Equal(SortedSites(omitted.Data!.Implementations), SortedSites(direct.Data!.Implementations));
        Assert.Equal(omitted.Data.TotalCount, direct.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalseWithImplementationFile_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var inherited = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("Inherited.cs"),
            Transitive = false
        });

        Assert.True(inherited.Success);
        var only = Assert.Single(inherited.Data!.Implementations);
        Assert.Equal("Relisted", only.Name);
        Assert.Equal(1, inherited.Data.TotalCount);
        Assert.False(inherited.Data.Truncated);

        var viaDerived = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            ImplementationFile = workspace.PathOf("ViaDerived.cs"),
            Transitive = false
        });

        Assert.True(viaDerived.Success);
        Assert.Empty(viaDerived.Data!.Implementations);
        Assert.Equal(0, viaDerived.Data.TotalCount);
        Assert.False(viaDerived.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalseWithMaxResults_CapsDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ChainSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            MaxResults = 2,
            Transitive = false
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.Implementations.Count);
        Assert.All(capped.Data.Implementations, i => Assert.Contains(i.Name, DirectChainImplementations));
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            MaxResults = 4,
            Transitive = false
        });

        Assert.True(exact.Success);
        Assert.Equal(DirectChainImplementations, SortedNames(exact.Data!.Implementations));
        Assert.Equal(4, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    // Extra App/AppC.cs source for the two-project fixture: P4 only inherits IRunner from P1, P5
    // implements it through IFastRunner (declared in App), and P6 re-lists IRunner on top of P2.
    private const string AppChainSource = """
        public class P4 : P1 { }
        public interface IFastRunner : IRunner { }
        public class P5 : IFastRunner { public void Run() { } }
        public class P6 : P2, IRunner { }
        """;

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalseWithProjectPath_FiltersDirectSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(extraAppSource: AppChainSource);
        var operation = new FindImplementationsOperation(workspace.Context);

        var appTransitive = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath
        });
        var appDirect = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            Transitive = false
        });
        var libDirect = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.LibProjectPath,
            Transitive = false
        });
        var solutionDirect = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            Transitive = false
        });

        Assert.True(appTransitive.Success);
        Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5", "P6" }, SortedNames(appTransitive.Data!.Implementations));
        Assert.True(appDirect.Success);
        Assert.Equal(new[] { "P1", "P2", "P3", "P6" }, SortedNames(appDirect.Data!.Implementations));
        Assert.Equal(4, appDirect.Data.TotalCount);
        Assert.False(appDirect.Data.Truncated);
        Assert.True(libDirect.Success);
        Assert.Equal("L1", Assert.Single(libDirect.Data!.Implementations).Name);
        Assert.True(solutionDirect.Success);
        Assert.Equal(new[] { "L1", "P1", "P2", "P3", "P6" }, SortedNames(solutionDirect.Data!.Implementations));
    }

    [SkippableFact]
    public async Task FindImplementations_TransitiveFalseWithProjectPathAndMaxResults_CapsCombinedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(extraAppSource: AppChainSource);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = "IRunner",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 1,
            Transitive = false
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Implementations);
        Assert.Contains(only.Name, new[] { "P1", "P2", "P3", "P6" });
        Assert.Equal(4, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    #endregion

    #region namespaceFilter

    // Contracts.cs declares App.Contracts.IRunner (Run). IRunner is implemented by S1 / S2
    // (App.Services), O1 and SubS1 (App.Services.Orders; SubS1 only inherits the implementation from
    // S1, so it declares no Run of its own), X1 (App.ServicesExtra: a string prefix of App.Services but
    // not nested in it), L1 (app.services: differs only by case) and G1 (global namespace).
    private static readonly Dictionary<string, string> NamespaceSources = new()
    {
        ["Contracts.cs"] = """
            namespace App.Contracts
            {
                public interface IRunner { void Run(); }
            }
            """,
        ["Services.cs"] = """
            using App.Contracts;
            namespace App.Services
            {
                public class S1 : IRunner { public virtual void Run() { } }
                public class S2 : IRunner { public void Run() { } }
            }
            """,
        ["Orders.cs"] = """
            using App.Contracts;
            namespace App.Services.Orders
            {
                public class O1 : IRunner { public void Run() { } }
                public class SubS1 : App.Services.S1 { }
            }
            """,
        ["Extra.cs"] = """
            namespace App.ServicesExtra
            {
                public class X1 : App.Contracts.IRunner { public void Run() { } }
            }
            """,
        ["Lower.cs"] = """
            namespace app.services
            {
                public class L1 : App.Contracts.IRunner { public void Run() { } }
            }
            """,
        ["Global.cs"] = """
            public class G1 : App.Contracts.IRunner { public void Run() { } }
            """
    };

    private static readonly string[] AllNamespaceImplementations = ["G1", "L1", "O1", "S1", "S2", "SubS1", "X1"];

    private static readonly string[] ServicesNamespaceImplementations = ["O1", "S1", "S2", "SubS1"];

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public async Task FindImplementations_OmittedBlankOrRootNamespaceFilter_ReturnsEveryImplementation(string? namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Equal(AllNamespaceImplementations, SortedNames(result.Data!.Implementations));
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Services")]
    [InlineData("  App.Services  ")]
    [InlineData("global::App.Services")]
    [InlineData(" global:: App.Services ")]
    public async Task FindImplementations_NamespaceFilter_ReturnsImplementationsInNamespaceAndNested(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Equal("IRunner", result.Data!.SymbolName);
        // X1 (App.ServicesExtra) shares the string prefix but is not nested; L1 differs by case; G1 is
        // in the global namespace.
        Assert.Equal(ServicesNamespaceImplementations, SortedNames(result.Data.Implementations));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Services.Orders", new[] { "O1", "SubS1" })]
    [InlineData("App", new[] { "O1", "S1", "S2", "SubS1", "X1" })]
    [InlineData("App.ServicesExtra", new[] { "X1" })]
    [InlineData("app.services", new[] { "L1" })]
    public async Task FindImplementations_NamespaceFilter_MatchesExactOrNestedNamespaceOrdinally(
        string namespaceFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, SortedNames(result.Data!.Implementations));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Serv")]
    [InlineData("Services")]
    [InlineData("App.Contracts")]
    [InlineData("Nope")]
    public async Task FindImplementations_NamespaceFilterNoMatch_ReturnsEmptySuccess(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Implementations);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NamespaceFilter_MemberTarget_UsesContainingTypeNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var all = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Run"
        });
        var filtered = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Services"
        });

        Assert.True(all.Success);
        // S1 / S2 / O1 / X1 / L1 / G1 each declare Run; SubS1 inherits S1.Run.
        Assert.Equal(6, all.Data!.TotalCount);

        Assert.True(filtered.Success);
        Assert.Equal(3, filtered.Data!.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.All(filtered.Data.Implementations, i => Assert.Equal("Run", i.Name));
        Assert.Equal(
            new[] { "App.Services.Orders.O1.Run()", "App.Services.S1.Run()", "App.Services.S2.Run()" },
            filtered.Data.Implementations.Select(i => i.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task FindImplementations_NamespaceFilterCombinedWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = "App.Services",
            Transitive = false
        });

        Assert.True(result.Success);
        // SubS1 only inherits the implementation from S1, so the direct set drops it.
        Assert.Equal(new[] { "O1", "S1", "S2" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NamespaceFilterCombinedWithImplementationFile_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var inOrders = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = "App.Services",
            ImplementationFile = workspace.PathOf("Orders.cs")
        });
        var inExtra = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = "App.Services",
            ImplementationFile = workspace.PathOf("Extra.cs")
        });

        Assert.True(inOrders.Success);
        Assert.Equal(new[] { "O1", "SubS1" }, SortedNames(inOrders.Data!.Implementations));
        Assert.All(inOrders.Data.Implementations, i => Assert.True(SamePath(i.File, workspace.PathOf("Orders.cs"))));
        Assert.Equal(2, inOrders.Data.TotalCount);
        Assert.False(inOrders.Data.Truncated);

        Assert.True(inExtra.Success);
        Assert.Empty(inExtra.Data!.Implementations);
        Assert.Equal(0, inExtra.Data.TotalCount);
        Assert.False(inExtra.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NamespaceFilterCombinedWithProjectPath_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = "App.Services.Orders",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "O1", "SubS1" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NamespaceFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            NamespaceFilter = "App.Services",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Implementations.Count);
        Assert.All(result.Data.Implementations, i => Assert.Contains(i.Name, ServicesNamespaceImplementations));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }


    #endregion

    #region accessibilityFilter

    // AccessibilitySources: IRunner + implementers covering every DeclaredAccessibility (and
    // implicit defaults). Nested types live on Host so protected / private / compounds are legal.
    // BaseDirect + DerivedOnly exercise transitive; ExplicitImpl is an explicit interface member
    // (DeclaredAccessibility.Private) for member-target coverage.
    private static readonly Dictionary<string, string> AccessibilitySources = new()
    {
        ["Contracts.cs"] = """
            namespace App.Contracts
            {
                public interface IRunner { void Run(); }
            }
            """,
        ["Public.cs"] = """
            namespace App
            {
                public class PubImpl : App.Contracts.IRunner { public void Run() { } }
            }
            """,
        ["Internal.cs"] = """
            namespace App
            {
                internal class IntImpl : App.Contracts.IRunner { public void Run() { } }
                class DefaultTop : App.Contracts.IRunner { public void Run() { } }
            }
            """,
        ["Nested.cs"] = """
            namespace App
            {
                public class Host
                {
                    public class NestedPub : App.Contracts.IRunner { public void Run() { } }
                    internal class NestedInt : App.Contracts.IRunner { public void Run() { } }
                    protected class NestedProt : App.Contracts.IRunner { public void Run() { } }
                    private class NestedPriv : App.Contracts.IRunner { public void Run() { } }
                    protected internal class NestedProtInt : App.Contracts.IRunner { public void Run() { } }
                    private protected class NestedPrivProt : App.Contracts.IRunner { public void Run() { } }
                    class NestedDefault : App.Contracts.IRunner { public void Run() { } }
                }
            }
            """,
        ["Inherited.cs"] = """
            namespace App
            {
                public class BaseDirect : App.Contracts.IRunner { public void Run() { } }
                public class DerivedOnly : BaseDirect { }
            }
            """,
        ["Explicit.cs"] = """
            namespace App
            {
                public class ExplicitImpl : App.Contracts.IRunner
                {
                    void App.Contracts.IRunner.Run() { }
                }
            }
            """
    };

    private static readonly string[] AllAccessibilityImplementations =
    [
        "BaseDirect", "DefaultTop", "DerivedOnly", "ExplicitImpl", "IntImpl", "NestedDefault",
        "NestedInt", "NestedPriv", "NestedPrivProt", "NestedProt", "NestedProtInt", "NestedPub",
        "PubImpl"
    ];

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindImplementations_OmittedOrBlankAccessibilityFilter_ReturnsEveryAccessibility(
        string? accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success);
        Assert.Equal(AllAccessibilityImplementations, SortedNames(result.Data!.Implementations));
        Assert.Equal(AllAccessibilityImplementations.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("public", new[] { "BaseDirect", "DerivedOnly", "ExplicitImpl", "NestedPub", "PubImpl" })]
    [InlineData("internal", new[] { "DefaultTop", "IntImpl", "NestedInt" })]
    [InlineData("protected", new[] { "NestedProt" })]
    [InlineData("private", new[] { "NestedDefault", "NestedPriv" })]
    [InlineData("protected internal", new[] { "NestedProtInt" })]
    [InlineData("private protected", new[] { "NestedPrivProt" })]
    public async Task FindImplementations_AccessibilityFilter_SelectsExactlyEachDeclaredAccessibility(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success, accessibilityFilter);
        Assert.Equal(expected, SortedNames(result.Data!.Implementations));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("PUBLIC", "public")]
    [InlineData("  Private  ", "private")]
    [InlineData("Protected   Internal", "protected internal")]
    [InlineData("internal protected", "protected internal")]
    [InlineData("protected private", "private protected")]
    public async Task FindImplementations_AccessibilityFilter_IsCaseInsensitiveAndAcceptsEitherModifierOrder(
        string accessibilityFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = accessibilityFilter
        });
        var expected = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.Equal(SortedNames(expected.Data!.Implementations), SortedNames(filtered.Data!.Implementations));
    }

    [SkippableTheory]
    [InlineData("friend")]
    [InlineData("ProtectedOrInternal")]
    [InlineData("public, internal")]
    public async Task FindImplementations_UnknownAccessibilityFilter_ThrowsInvalidVisibility(
        string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FindImplementationsParams
            {
                SourceFile = workspace.PathOf("Contracts.cs"),
                SymbolName = "IRunner",
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.StartsWith($"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilter_MemberTarget_UsesMemberDeclaredAccessibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var all = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Run"
        });
        var publics = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "public"
        });
        var privates = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "private"
        });

        Assert.True(all.Success);
        // Every type that declares Run (not DerivedOnly, which inherits BaseDirect.Run).
        Assert.Equal(12, all.Data!.TotalCount);

        Assert.True(publics.Success);
        // Public interface method implementations (not ExplicitImpl's private explicit member).
        Assert.Equal(11, publics.Data!.TotalCount);
        Assert.DoesNotContain(publics.Data.Implementations, i => i.FullyQualifiedName.Contains("ExplicitImpl"));
        Assert.All(publics.Data.Implementations, i => Assert.Equal("Run", i.Name));

        Assert.True(privates.Success);
        Assert.Equal(1, privates.Data!.TotalCount);
        Assert.Contains("ExplicitImpl", privates.Data.Implementations[0].FullyQualifiedName);
        Assert.False(privates.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilterCombinedWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "public",
            Transitive = false
        });

        Assert.True(result.Success);
        // DerivedOnly only inherits the implementation from BaseDirect, so the direct public set
        // drops it.
        Assert.Equal(new[] { "BaseDirect", "ExplicitImpl", "NestedPub", "PubImpl" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilterCombinedWithImplementationFile_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var inPublic = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "public",
            ImplementationFile = workspace.PathOf("Public.cs")
        });
        var inNested = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "public",
            ImplementationFile = workspace.PathOf("Nested.cs")
        });

        Assert.True(inPublic.Success);
        Assert.Equal(new[] { "PubImpl" }, SortedNames(inPublic.Data!.Implementations));
        Assert.Equal(1, inPublic.Data.TotalCount);

        Assert.True(inNested.Success);
        Assert.Equal(new[] { "NestedPub" }, SortedNames(inNested.Data!.Implementations));
        Assert.Equal(1, inNested.Data.TotalCount);
        Assert.False(inNested.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilterCombinedWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "public",
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        // Contracts.IRunner's implementers under App (not App.Contracts). ExplicitImpl is public type.
        Assert.Equal(new[] { "BaseDirect", "DerivedOnly", "ExplicitImpl", "NestedPub", "PubImpl" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(5, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "public",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Implementations.Count);
        Assert.Equal(5, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(result.Data.Implementations, i =>
            Assert.Contains(i.Name, new[] { "BaseDirect", "DerivedOnly", "ExplicitImpl", "NestedPub", "PubImpl" }));
    }

    [SkippableFact]
    public async Task FindImplementations_AccessibilityFilterWithNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["IRunner.cs"] = "public interface IRunner { void Run(); }",
            ["OnlyPublic.cs"] = "public class OnlyPub : IRunner { public void Run() { } }"
        });
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("IRunner.cs"),
            SymbolName = "IRunner",
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Implementations);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region kindFilter

    // KindSources: IShape implemented by classes (Circle, Square, BigCircle inheriting from Circle),
    // structs (Point, Pixel), and a record (Tri), spread over namespaces / files / accessibilities so
    // kindFilter can be combined with the other filters. Draw (method) and Sides (property) are the
    // member targets.
    private static readonly Dictionary<string, string> KindSources = new()
    {
        ["Contracts.cs"] = """
            namespace App.Contracts
            {
                public interface IShape { void Draw(); int Sides { get; } }
            }
            """,
        ["Classes.cs"] = """
            namespace App.Shapes
            {
                public class Circle : App.Contracts.IShape { public void Draw() { } public int Sides => 0; }
                internal class Square : App.Contracts.IShape { public void Draw() { } public int Sides => 4; }
                public class BigCircle : Circle { }
            }
            """,
        ["Structs.cs"] = """
            namespace App.Shapes.Values
            {
                public struct Point : App.Contracts.IShape { public void Draw() { } public int Sides => 0; }
                internal struct Pixel : App.Contracts.IShape { public void Draw() { } public int Sides => 4; }
            }
            """,
        ["Records.cs"] = """
            namespace App.Records
            {
                public record Tri : App.Contracts.IShape { public void Draw() { } public int Sides => 3; }
            }
            """
    };

    private static readonly string[] AllKindImplementations =
        ["BigCircle", "Circle", "Pixel", "Point", "Square", "Tri"];

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindImplementations_OmittedOrBlankKindFilter_ReturnsEveryKind(string? kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.Equal(AllKindImplementations, SortedNames(result.Data!.Implementations));
        Assert.Equal(AllKindImplementations.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Class", new[] { "BigCircle", "Circle", "Square" })]
    [InlineData("Struct", new[] { "Pixel", "Point" })]
    [InlineData("Record", new[] { "Tri" })]
    [InlineData("Interface", new string[0])]
    [InlineData("Method", new string[0])]
    [InlineData("Local", new string[0])]
    [InlineData("Namespace", new string[0])]
    public async Task FindImplementations_KindFilter_KeepsOnlyImplementationsOfThatKind(
        string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = kindFilter
        });

        Assert.True(result.Success, kindFilter);
        Assert.Equal(expected, SortedNames(result.Data!.Implementations));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.All(result.Data.Implementations, i => Assert.Equal(kindFilter, i.Kind.ToString()));
    }

    [SkippableTheory]
    [InlineData("STRUCT", "Struct")]
    [InlineData("  record  ", "Record")]
    [InlineData("class", "Class")]
    public async Task FindImplementations_KindFilter_IsCaseInsensitiveAndTrimmed(string kindFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = kindFilter
        });
        var expected = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.Implementations);
        Assert.Equal(SortedNames(expected.Data.Implementations), SortedNames(filtered.Data!.Implementations));
    }

    [SkippableTheory]
    [InlineData("Klass")]
    [InlineData("6")]
    [InlineData("Class, Struct")]
    public async Task FindImplementations_UnknownKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FindImplementationsParams
            {
                SourceFile = workspace.PathOf("Contracts.cs"),
                SymbolName = "IShape",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilter_MemberTargets_MatchImplementingMemberKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        async Task<FindImplementationsResult> Run(string symbolName, string? kindFilter)
        {
            var result = await operation.ExecuteAsync(new FindImplementationsParams
            {
                SourceFile = workspace.PathOf("Contracts.cs"),
                SymbolName = symbolName,
                KindFilter = kindFilter
            });
            Assert.True(result.Success, $"{symbolName}/{kindFilter}");
            return result.Data!;
        }

        var drawAll = await Run("Draw", null);
        var drawMethods = await Run("Draw", "method");
        var drawProperties = await Run("Draw", "Property");
        var sidesProperties = await Run("Sides", "Property");
        var sidesMethods = await Run("Sides", "Method");

        // Every type declaring Draw (BigCircle inherits Circle.Draw).
        Assert.Equal(5, drawAll.TotalCount);
        Assert.Equal(5, drawMethods.TotalCount);
        Assert.All(drawMethods.Implementations, i => Assert.Equal("Draw", i.Name));
        Assert.Empty(drawProperties.Implementations);
        Assert.Equal(0, drawProperties.TotalCount);

        Assert.Equal(5, sidesProperties.TotalCount);
        Assert.All(sidesProperties.Implementations, i => Assert.Equal("Sides", i.Name));
        Assert.Empty(sidesMethods.Implementations);
        Assert.Equal(0, sidesMethods.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilterCombinedWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var structsUnderShapes = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Struct",
            NamespaceFilter = "App.Shapes"
        });
        var recordsUnderShapes = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Record",
            NamespaceFilter = "App.Shapes"
        });

        Assert.True(structsUnderShapes.Success);
        // Structs live in App.Shapes.Values, nested under App.Shapes.
        Assert.Equal(new[] { "Pixel", "Point" }, SortedNames(structsUnderShapes.Data!.Implementations));
        Assert.Equal(2, structsUnderShapes.Data.TotalCount);

        Assert.True(recordsUnderShapes.Success);
        Assert.Empty(recordsUnderShapes.Data!.Implementations);
        Assert.Equal(0, recordsUnderShapes.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilterCombinedWithAccessibilityFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Class",
            AccessibilityFilter = "internal"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Square" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilterCombinedWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Class",
            Transitive = false
        });

        Assert.True(result.Success);
        // BigCircle only inherits the implementation from Circle, so the direct class set drops it.
        Assert.Equal(new[] { "Circle", "Square" }, SortedNames(result.Data!.Implementations));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilterCombinedWithImplementationFile_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var classesInStructsFile = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Class",
            ImplementationFile = workspace.PathOf("Structs.cs")
        });
        var structsInStructsFile = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Struct",
            ImplementationFile = workspace.PathOf("Structs.cs")
        });

        Assert.True(classesInStructsFile.Success);
        Assert.Empty(classesInStructsFile.Data!.Implementations);
        Assert.Equal(0, classesInStructsFile.Data.TotalCount);

        Assert.True(structsInStructsFile.Success);
        Assert.Equal(new[] { "Pixel", "Point" }, SortedNames(structsInStructsFile.Data!.Implementations));
        Assert.Equal(2, structsInStructsFile.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_KindFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            KindFilter = "Class",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Implementations.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(result.Data.Implementations, i =>
            Assert.Contains(i.Name, new[] { "BigCircle", "Circle", "Square" }));
    }

    #endregion

    #region nameFilter

    // NameSources: IRepository implemented by Sql* classes (one internal, one inheriting the
    // implementation), a Memory* class, a struct whose name contains "Sql" in the middle, and a class
    // with explicit interface implementations, spread over namespaces / files / accessibilities / kinds
    // so nameFilter can be combined with the other filters. Save (method) and Count (property) are the
    // member targets.
    private static readonly Dictionary<string, string> NameSources = new()
    {
        ["Contracts.cs"] = """
            namespace App.Data
            {
                public interface IRepository { void Save(); int Count { get; } }
            }
            """,
        ["Sql.cs"] = """
            namespace App.Data.Sql
            {
                public class SqlOrderRepository : App.Data.IRepository { public void Save() { } public int Count => 0; }
                internal class SqlUserRepository : App.Data.IRepository { public void Save() { } public int Count => 1; }
                public class CachedSqlOrderRepository : SqlOrderRepository { }
            }
            """,
        ["Memory.cs"] = """
            namespace App.Data.Memory
            {
                public class MemoryOrderRepository : App.Data.IRepository { public void Save() { } public int Count => 2; }
                public struct OrderSqlSnapshot : App.Data.IRepository { public void Save() { } public int Count => 3; }
            }
            """,
        ["Explicit.cs"] = """
            namespace App.Data.Explicit
            {
                public class ExplicitRepository : App.Data.IRepository
                {
                    void App.Data.IRepository.Save() { }
                    int App.Data.IRepository.Count => 4;
                }
            }
            """
    };

    private static readonly string[] AllNameImplementations =
    [
        "CachedSqlOrderRepository", "ExplicitRepository", "MemoryOrderRepository",
        "OrderSqlSnapshot", "SqlOrderRepository", "SqlUserRepository"
    ];

    private static async Task<FindImplementationsResult> RunNameFilterAsync(
        TempWorkspace workspace, FindImplementationsParams @params)
    {
        var operation = new FindImplementationsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(@params);
        Assert.True(result.Success, $"{@params.SymbolName}/{@params.NameFilter}");
        return result.Data!;
    }

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.NameFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task FindImplementations_OmittedOrBlankNameFilter_ReturnsEveryImplementation(string? nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = nameFilter
        });

        Assert.Equal(AllNameImplementations, SortedNames(data.Implementations));
        Assert.Equal(AllNameImplementations.Length, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Sql", new[] { "CachedSqlOrderRepository", "OrderSqlSnapshot", "SqlOrderRepository", "SqlUserRepository" })]
    [InlineData("Order", new[] { "CachedSqlOrderRepository", "MemoryOrderRepository", "OrderSqlSnapshot", "SqlOrderRepository" })]
    [InlineData("Repository", new[] { "CachedSqlOrderRepository", "ExplicitRepository", "MemoryOrderRepository", "SqlOrderRepository", "SqlUserRepository" })]
    [InlineData("SqlUserRepository", new[] { "SqlUserRepository" })]
    [InlineData("Snapshot", new[] { "OrderSqlSnapshot" })]
    [InlineData("Postgres", new string[0])]
    [InlineData("IRepository", new string[0])]
    public async Task FindImplementations_NameFilter_KeepsOnlyImplementationsWhoseNameContainsIt(
        string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = nameFilter
        });

        Assert.Equal(expected, SortedNames(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
        Assert.False(data.Truncated);
        Assert.All(data.Implementations, i => Assert.Contains(nameFilter, i.Name, StringComparison.OrdinalIgnoreCase));
    }

    [SkippableTheory]
    [InlineData("SQL")]
    [InlineData("sql")]
    [InlineData("  sQl  ")]
    [InlineData("\tSql\n")]
    public async Task FindImplementations_NameFilter_IsCaseInsensitiveAndTrimmed(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = nameFilter
        });

        Assert.Equal(
            new[] { "CachedSqlOrderRepository", "OrderSqlSnapshot", "SqlOrderRepository", "SqlUserRepository" },
            SortedNames(data.Implementations));
        Assert.Equal(4, data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilter_InnerWhitespaceIsNotCollapsed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql Order"
        });

        Assert.Empty(data.Implementations);
        Assert.Equal(0, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilter_MemberTargets_MatchImplementingMemberName()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        Task<FindImplementationsResult> Run(string symbolName, string? nameFilter) =>
            RunNameFilterAsync(workspace, new FindImplementationsParams
            {
                SourceFile = workspace.PathOf("Contracts.cs"),
                SymbolName = symbolName,
                NameFilter = nameFilter
            });

        var saveAll = await Run("Save", null);
        var saveBySave = await Run("Save", "sav");
        var saveByInterface = await Run("Save", "IRepository");
        var saveByCount = await Run("Save", "Count");
        var countByCount = await Run("Count", "COUNT");

        // Every type declaring Save (CachedSqlOrderRepository inherits SqlOrderRepository.Save),
        // including the explicit implementation.
        Assert.Equal(5, saveAll.TotalCount);
        Assert.Equal(5, saveBySave.TotalCount);
        Assert.Equal(SortedNames(saveAll.Implementations), SortedNames(saveBySave.Implementations));

        // An explicit interface implementation reports Roslyn's qualified metadata name, which is what
        // nameFilter matches against.
        var explicitSave = Assert.Single(saveByInterface.Implementations);
        Assert.Equal("App.Data.IRepository.Save", explicitSave.Name);
        Assert.Equal(1, saveByInterface.TotalCount);

        Assert.Empty(saveByCount.Implementations);
        Assert.Equal(0, saveByCount.TotalCount);

        Assert.Equal(5, countByCount.TotalCount);
        Assert.All(countByCount.Implementations, i => Assert.EndsWith("Count", i.Name, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterCombinedWithKindFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var sqlStructs = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            KindFilter = "Struct"
        });
        var sqlClasses = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            KindFilter = "Class"
        });

        Assert.Equal(new[] { "OrderSqlSnapshot" }, SortedNames(sqlStructs.Implementations));
        Assert.Equal(1, sqlStructs.TotalCount);
        Assert.Equal(
            new[] { "CachedSqlOrderRepository", "SqlOrderRepository", "SqlUserRepository" },
            SortedNames(sqlClasses.Implementations));
        Assert.Equal(3, sqlClasses.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterCombinedWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var sqlInMemory = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            NamespaceFilter = "App.Data.Memory"
        });
        var explicitInSql = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Explicit",
            NamespaceFilter = "App.Data.Sql"
        });

        Assert.Equal(new[] { "OrderSqlSnapshot" }, SortedNames(sqlInMemory.Implementations));
        Assert.Equal(1, sqlInMemory.TotalCount);
        Assert.Empty(explicitInSql.Implementations);
        Assert.Equal(0, explicitInSql.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterCombinedWithAccessibilityFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            AccessibilityFilter = "internal"
        });

        Assert.Equal(new[] { "SqlUserRepository" }, SortedNames(data.Implementations));
        Assert.Equal(1, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterCombinedWithTransitiveFalse_FiltersDirectSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            Transitive = false
        });

        // CachedSqlOrderRepository only inherits the implementation, so the direct set drops it.
        Assert.Equal(
            new[] { "OrderSqlSnapshot", "SqlOrderRepository", "SqlUserRepository" },
            SortedNames(data.Implementations));
        Assert.Equal(3, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterCombinedWithImplementationFile_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var ordersInSqlFile = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "order",
            ImplementationFile = workspace.PathOf("Sql.cs")
        });
        var memoryInSqlFile = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Memory",
            ImplementationFile = workspace.PathOf("Sql.cs")
        });

        Assert.Equal(
            new[] { "CachedSqlOrderRepository", "SqlOrderRepository" },
            SortedNames(ordersInSqlFile.Implementations));
        Assert.Equal(2, ordersInSqlFile.TotalCount);
        Assert.Empty(memoryInSqlFile.Implementations);
        Assert.Equal(0, memoryInSqlFile.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Sql",
            MaxResults = 2
        });

        Assert.Equal(2, data.Implementations.Count);
        Assert.Equal(4, data.TotalCount);
        Assert.True(data.Truncated);
        Assert.All(data.Implementations, i =>
            Assert.Contains(i.Name, new[] { "CachedSqlOrderRepository", "OrderSqlSnapshot", "SqlOrderRepository", "SqlUserRepository" }));
    }

    [SkippableFact]
    public async Task FindImplementations_NameFilterSingleMatchWithinMaxResults_IsNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);

        var data = await RunNameFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IRepository",
            NameFilter = "Snapshot",
            MaxResults = 1
        });

        Assert.Equal(new[] { "OrderSqlSnapshot" }, SortedNames(data.Implementations));
        Assert.Equal(1, data.TotalCount);
        Assert.False(data.Truncated);
    }

    #endregion

    #region StaticFilter (#3095)

    // StaticSources: IShape has a static abstract method (Create), a static virtual property (Label),
    // and an instance method (Area). Circle (public class) and Square (internal struct) implement
    // them implicitly; Hexagon implements Create and Area explicitly (private). Implementing types are
    // never static, so type targets keep everything for false and nothing for true.
    private static readonly Dictionary<string, string> StaticSources = new()
    {
        ["Contracts.cs"] = """
            namespace App.Shapes
            {
                public interface IShape
                {
                    static abstract IShape Create();
                    static virtual string Label => "shape";
                    double Area();
                }
            }
            """,
        ["Shapes.cs"] = """
            namespace App.Shapes.Impl
            {
                public class Circle : App.Shapes.IShape
                {
                    public static App.Shapes.IShape Create() => new Circle();
                    public static string Label => "circle";
                    public double Area() => 3.14;
                }

                internal struct Square : App.Shapes.IShape
                {
                    public static App.Shapes.IShape Create() => new Square();
                    public double Area() => 1;
                }

                public class Hexagon : App.Shapes.IShape
                {
                    static App.Shapes.IShape App.Shapes.IShape.Create() => new Hexagon();
                    double App.Shapes.IShape.Area() => 6;
                }
            }
            """
    };

    private static async Task<FindImplementationsResult> RunStaticFilterAsync(
        TempWorkspace workspace, FindImplementationsParams @params)
    {
        var operation = new FindImplementationsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(@params);
        Assert.True(result.Success, $"{@params.SymbolName}/{@params.StaticFilter}");
        return result.Data!;
    }

    // Containing type of each reported implementation (types report themselves), sorted.
    private static List<string> SortedOwners(IEnumerable<ImplementationInfo> implementations) =>
        implementations
            .Select(i => i.FullyQualifiedName)
            .Select(fqn => new[] { "Circle", "Square", "Hexagon" }.Single(t => fqn.Contains("App.Shapes.Impl." + t, StringComparison.Ordinal)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void StaticFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.StaticFilter);
    }

    [SkippableTheory]
    [InlineData(null, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData(false, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData(true, new string[0])]
    public async Task FindImplementations_StaticFilter_TypeTarget_ImplementingTypesAreNeverStatic(
        bool? staticFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "IShape",
            StaticFilter = staticFilter
        });

        Assert.Equal(expected, SortedNames(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Create", null, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData("Create", true, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData("Create", false, new string[0])]
    [InlineData("Label", true, new[] { "Circle" })]
    [InlineData("Label", false, new string[0])]
    [InlineData("Area", null, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData("Area", false, new[] { "Circle", "Hexagon", "Square" })]
    [InlineData("Area", true, new string[0])]
    public async Task FindImplementations_StaticFilter_MemberTarget_KeepsOnlyMatchingStaticness(
        string member, bool? staticFilter, string[] expectedOwners)
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = member,
            StaticFilter = staticFilter
        });

        Assert.Equal(expectedOwners, SortedOwners(data.Implementations));
        Assert.Equal(expectedOwners.Length, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableTheory]
    [InlineData("public", new[] { "Circle", "Square" })]
    [InlineData("private", new[] { "Hexagon" })]
    public async Task FindImplementations_StaticFilter_CombinesWithAccessibilityFilter(
        string accessibilityFilter, string[] expectedOwners)
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Create",
            StaticFilter = true,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.Equal(expectedOwners, SortedOwners(data.Implementations));
        Assert.Equal(expectedOwners.Length, data.TotalCount);
    }

    [SkippableFact]
    public async Task FindImplementations_StaticFilter_AppliedBeforeMaxResults()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Create",
            StaticFilter = true,
            MaxResults = 1
        });

        Assert.Single(data.Implementations);
        Assert.Equal(3, data.TotalCount);
        Assert.True(data.Truncated);

        var instance = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Create",
            StaticFilter = false,
            MaxResults = 1
        });

        Assert.Empty(instance.Implementations);
        Assert.Equal(0, instance.TotalCount);
        Assert.False(instance.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_StaticFilter_CombinesWithImplementationFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);

        var inShapes = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Create",
            StaticFilter = true,
            ImplementationFile = workspace.PathOf("Shapes.cs")
        });
        Assert.Equal(new[] { "Circle", "Hexagon", "Square" }, SortedOwners(inShapes.Implementations));
        Assert.Equal(3, inShapes.TotalCount);

        var inContracts = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Contracts.cs"),
            SymbolName = "Create",
            StaticFilter = true,
            ImplementationFile = workspace.PathOf("Contracts.cs")
        });
        Assert.Empty(inContracts.Implementations);
        Assert.Equal(0, inContracts.TotalCount);
    }

    private const string StaticEdgeSource = """
        namespace App.Edges
        {
            public interface IBase
            {
                static abstract int SCreate();
                static virtual int SLabel() => 0;
                int Area();
                void Dim() { }
            }
            public interface IDerived : IBase
            {
                static int IBase.SLabel() => 1;
                void IBase.Dim() { }
            }
            public record Rec : IBase { public static int SCreate() => 1; public int Area() => 1; }
            public record struct RecS : IBase { public static int SCreate() => 2; public int Area() => 2; }
            public class Outer { public class Nested : IBase { public static int SCreate() => 3; public int Area() => 3; } }
            public partial class Part : IBase { public static partial int SCreate(); public partial int Area(); }
            public partial class Part { public static partial int SCreate() => 4; public partial int Area() => 4; }
            public class Gen<T> : IBase { public static int SCreate() => 5; public int Area() => 5; }
            public interface ISelf<TSelf> where TSelf : ISelf<TSelf> { static abstract TSelf Make(); TSelf Twin(); }
            public struct Self : ISelf<Self> { public static Self Make() => default; public Self Twin() => this; }
            public class GenSelf<T> : ISelf<GenSelf<T>> { public static GenSelf<T> Make() => new(); public GenSelf<T> Twin() => this; }
            public static class Util { public static void Go() { } }
            public abstract class Animal { public abstract int Legs(); public virtual int Eyes() => 2; }
            public class Dog : Animal { public override int Legs() => 4; public override int Eyes() => 2; }
            public abstract class GB<T> { public abstract T GM(); }
            public class GD : GB<int> { public override int GM() => 1; }
        }
        """;

    // StaticEdgeSource line / column of the first occurrence of token on that 1-based line.
    private static (int Line, int Column) StaticEdgePosition(int line, string token)
    {
        var text = StaticEdgeSource.Split('\n')[line - 1];
        var index = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{token} not on line {line}");
        return (line, index + 1);
    }

    // Edge kinds: default interface implementations re-implemented explicitly in a derived interface
    // (IDerived: static SLabel, instance Dim), records, record structs, nested types, partial members,
    // generic implementers, and a self-referencing generic static abstract (ISelf<TSelf>). Each
    // implementation is judged on its own IsStatic, which C# forces to equal the target's, so a member
    // target is all-or-nothing; implementing types are never static. Derived interfaces are not
    // returned for a type target. Static-class targets (Util / Util.Go) and abstract / virtual class
    // targets with overrides (Animal, Animal.Legs / Eyes, GB<T>, GB<T>.GM) return nothing even with no
    // filter: this pins current behavior (Roslyn FindImplementationsAsync does not report derived
    // classes or overrides), not the intended contract.
    [SkippableTheory]
    [InlineData(3, "IBase", null, new[] { "App.Edges.Gen<T>", "App.Edges.Outer.Nested", "App.Edges.Part", "App.Edges.Rec", "App.Edges.RecS" })]
    [InlineData(3, "IBase", false, new[] { "App.Edges.Gen<T>", "App.Edges.Outer.Nested", "App.Edges.Part", "App.Edges.Rec", "App.Edges.RecS" })]
    [InlineData(3, "IBase", true, new string[0])]
    [InlineData(5, "SCreate", null, new[] { "App.Edges.Gen<T>.SCreate()", "App.Edges.Outer.Nested.SCreate()", "App.Edges.Part.SCreate()", "App.Edges.Rec.SCreate()", "App.Edges.RecS.SCreate()" })]
    [InlineData(5, "SCreate", true, new[] { "App.Edges.Gen<T>.SCreate()", "App.Edges.Outer.Nested.SCreate()", "App.Edges.Part.SCreate()", "App.Edges.Rec.SCreate()", "App.Edges.RecS.SCreate()" })]
    [InlineData(5, "SCreate", false, new string[0])]
    [InlineData(6, "SLabel", null, new[] { "App.Edges.IDerived.App.Edges.IBase.SLabel()" })]
    [InlineData(6, "SLabel", true, new[] { "App.Edges.IDerived.App.Edges.IBase.SLabel()" })]
    [InlineData(6, "SLabel", false, new string[0])]
    [InlineData(7, "Area", null, new[] { "App.Edges.Gen<T>.Area()", "App.Edges.Outer.Nested.Area()", "App.Edges.Part.Area()", "App.Edges.Rec.Area()", "App.Edges.RecS.Area()" })]
    [InlineData(7, "Area", false, new[] { "App.Edges.Gen<T>.Area()", "App.Edges.Outer.Nested.Area()", "App.Edges.Part.Area()", "App.Edges.Rec.Area()", "App.Edges.RecS.Area()" })]
    [InlineData(7, "Area", true, new string[0])]
    [InlineData(8, "Dim", null, new[] { "App.Edges.IDerived.App.Edges.IBase.Dim()" })]
    [InlineData(8, "Dim", false, new[] { "App.Edges.IDerived.App.Edges.IBase.Dim()" })]
    [InlineData(8, "Dim", true, new string[0])]
    [InlineData(21, "ISelf", null, new[] { "App.Edges.GenSelf<T>", "App.Edges.Self" })]
    [InlineData(21, "ISelf", false, new[] { "App.Edges.GenSelf<T>", "App.Edges.Self" })]
    [InlineData(21, "ISelf", true, new string[0])]
    [InlineData(21, "Make", null, new[] { "App.Edges.GenSelf<T>.Make()", "App.Edges.Self.Make()" })]
    [InlineData(21, "Make", true, new[] { "App.Edges.GenSelf<T>.Make()", "App.Edges.Self.Make()" })]
    [InlineData(21, "Make", false, new string[0])]
    [InlineData(21, "Twin", null, new[] { "App.Edges.GenSelf<T>.Twin()", "App.Edges.Self.Twin()" })]
    [InlineData(21, "Twin", false, new[] { "App.Edges.GenSelf<T>.Twin()", "App.Edges.Self.Twin()" })]
    [InlineData(21, "Twin", true, new string[0])]
    [InlineData(24, "Util", null, new string[0])]
    [InlineData(24, "Util", true, new string[0])]
    [InlineData(24, "Util", false, new string[0])]
    [InlineData(24, "Go", null, new string[0])]
    [InlineData(24, "Go", true, new string[0])]
    [InlineData(24, "Go", false, new string[0])]
    [InlineData(25, "Animal", null, new string[0])]
    [InlineData(25, "Animal", true, new string[0])]
    [InlineData(25, "Animal", false, new string[0])]
    [InlineData(25, "Legs", null, new string[0])]
    [InlineData(25, "Legs", true, new string[0])]
    [InlineData(25, "Legs", false, new string[0])]
    [InlineData(25, "Eyes", null, new string[0])]
    [InlineData(25, "Eyes", true, new string[0])]
    [InlineData(25, "Eyes", false, new string[0])]
    [InlineData(27, "GB", null, new string[0])]
    [InlineData(27, "GB", true, new string[0])]
    [InlineData(27, "GB", false, new string[0])]
    [InlineData(27, "GM", null, new string[0])]
    [InlineData(27, "GM", true, new string[0])]
    [InlineData(27, "GM", false, new string[0])]
    public async Task FindImplementations_StaticFilter_EdgeKinds(
        int line, string token, bool? staticFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Edges.cs"] = StaticEdgeSource });
        var (targetLine, targetColumn) = StaticEdgePosition(line, token);

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Edges.cs"),
            Line = targetLine,
            Column = targetColumn,
            StaticFilter = staticFilter
        });

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            data.Implementations.Select(i => i.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(expected.Length, data.TotalCount);
        Assert.False(data.Truncated);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task FindImplementations_StaticFilter_PartialMember_ReportedOnceAtDefiningDeclaration(bool? staticFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Edges.cs"] = StaticEdgeSource });
        var (targetLine, targetColumn) = StaticEdgePosition(5, "SCreate");

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Edges.cs"),
            Line = targetLine,
            Column = targetColumn,
            StaticFilter = staticFilter
        });

        var part = Assert.Single(data.Implementations, i => i.FullyQualifiedName == "App.Edges.Part.SCreate()");
        Assert.Equal(18, part.Line);
    }

    // Implementations in other projects of the solution are returned and filtered like local ones.
    [SkippableTheory]
    [InlineData("IRunner", null, new[] { "L1", "P1", "P2", "P3" })]
    [InlineData("IRunner", false, new[] { "L1", "P1", "P2", "P3" })]
    [InlineData("IRunner", true, new string[0])]
    [InlineData("Run", null, new[] { "L1.Run()", "P1.Run()", "P2.Run()", "P3.Run()" })]
    [InlineData("Run", false, new[] { "L1.Run()", "P1.Run()", "P2.Run()", "P3.Run()" })]
    [InlineData("Run", true, new string[0])]
    public async Task FindImplementations_StaticFilter_OtherProjects_AreReturnedAndFiltered(
        string symbolName, bool? staticFilter, string[] expected)
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = symbolName,
            StaticFilter = staticFilter
        });

        Assert.True(result.Success);
        Assert.Equal(
            expected,
            result.Data!.Implementations.Select(i => i.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    // Implementations declared only in metadata are never listed. For a metadata target (here
    // System.IDisposable) they are still counted in TotalCount when no location filter applies, so
    // Truncated is true; staticFilter true drops them all (implementing types are never static).
    // Pins current behavior.
    [SkippableTheory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindImplementations_StaticFilter_MetadataImplementations_AreNotListed(bool? staticFilter)
    {
        const string source = """
            namespace App.Meta
            {
                public class Disp : System.IDisposable { public void Dispose() { } }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Meta.cs"] = source });

        var data = await RunStaticFilterAsync(workspace, new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Meta.cs"),
            Line = 3,
            Column = source.Split('\n')[2].IndexOf("IDisposable", StringComparison.Ordinal) + 1,
            StaticFilter = staticFilter
        });

        Assert.Equal("System.IDisposable", data.FullyQualifiedName);
        if (staticFilter == true)
        {
            Assert.Empty(data.Implementations);
            Assert.Equal(0, data.TotalCount);
            Assert.False(data.Truncated);
        }
        else
        {
            var only = Assert.Single(data.Implementations);
            Assert.Equal("App.Meta.Disp", only.FullyQualifiedName);
            Assert.True(data.TotalCount > 1);
            Assert.True(data.Truncated);
        }
    }

    #endregion

    #region AbstractFilter (#3136)

    // AbstractSource: IRunner (Run / Speed / Done) is implemented by abstract classes (BaseRunner with
    // abstract members; EmptyAbs and Explicit with no abstract members), an abstract record (AbsRec),
    // a nested abstract class, an abstract generic class, and by concrete types: a sealed class, a
    // struct, a class with a virtual member, a class with an extern member, plus FastRunner / ConRec,
    // which inherit the interface from an abstract base (reported for type targets only when
    // transitive is true). IReAbs re-abstracts IRunner.Run and IDim re-implements it with a default
    // body. IStat has a static abstract member implemented by an abstract class. Animal / Dog pin the
    // current empty result for class targets.
    private const string AbstractSource = """
        namespace App.Abs
        {
            public interface IRunner { void Run(); int Speed { get; } event System.Action Done; }
            public abstract class BaseRunner : IRunner { public abstract void Run(); public abstract int Speed { get; } public abstract event System.Action Done; }
            public class FastRunner : BaseRunner { public override void Run() { } public override int Speed => 2; public override event System.Action Done { add { } remove { } } }
            public sealed class SealedRunner : IRunner { public void Run() { } public int Speed => 1; public event System.Action Done { add { } remove { } } }
            public abstract class EmptyAbs : IRunner { public void Run() { } public int Speed => 0; public event System.Action Done { add { } remove { } } }
            public abstract record AbsRec : IRunner { public abstract void Run(); public int Speed => 3; public event System.Action Done { add { } remove { } } }
            public record ConRec : AbsRec { public override void Run() { } }
            public struct SRunner : IRunner { public void Run() { } public int Speed => 4; public event System.Action Done { add { } remove { } } }
            public abstract class Explicit : IRunner { void IRunner.Run() { } int IRunner.Speed => 5; event System.Action IRunner.Done { add { } remove { } } }
            public interface IReAbs : IRunner { abstract void IRunner.Run(); }
            public interface IDim : IRunner { void IRunner.Run() { } }
            public class Virt : IRunner { public virtual void Run() { } public int Speed => 6; public event System.Action Done { add { } remove { } } }
            public abstract class Outer { public abstract class NestedAbs : IRunner { public abstract void Run(); public int Speed => 7; public event System.Action Done { add { } remove { } } } }
            public abstract class GenAbs<T> : IRunner { public abstract void Run(); public int Speed => 8; public event System.Action Done { add { } remove { } } }
            public interface IStat { static abstract int Make(); }
            public abstract class StatImpl : IStat { public static int Make() => 1; }
            public class ExternImpl : IRunner { [System.Runtime.InteropServices.DllImport("x")] public static extern void Ext(); public void Run() { } public int Speed => 9; public event System.Action Done { add { } remove { } } }
            public abstract class Animal { public abstract int Legs(); }
            public abstract class Dog : Animal { }
        }
        """;

    private static readonly string[] AbstractTypes =
    {
        "App.Abs.AbsRec", "App.Abs.BaseRunner", "App.Abs.EmptyAbs", "App.Abs.Explicit",
        "App.Abs.GenAbs<T>", "App.Abs.Outer.NestedAbs"
    };

    private static readonly string[] DirectConcreteTypes =
    {
        "App.Abs.ExternImpl", "App.Abs.SRunner", "App.Abs.SealedRunner", "App.Abs.Virt"
    };

    // AbstractSource line / column of the first occurrence of token on that 1-based line.
    private static (int Line, int Column) AbstractPosition(int line, string token)
    {
        var text = AbstractSource.Split('\n')[line - 1];
        var index = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{token} not on line {line}");
        return (line, index + 1);
    }

    private static async Task<FindImplementationsResult> RunAbstractFilterAsync(
        TempWorkspace workspace, int line, string token, bool? abstractFilter,
        Func<FindImplementationsParams, FindImplementationsParams>? configure = null)
    {
        var (targetLine, targetColumn) = AbstractPosition(line, token);
        var @params = new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Abs.cs"),
            Line = targetLine,
            Column = targetColumn,
            AbstractFilter = abstractFilter
        };
        if (configure != null) @params = configure(@params);

        var operation = new FindImplementationsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(@params);
        Assert.True(result.Success, $"{token}/{abstractFilter}: {result.Error?.Message}");
        return result.Data!;
    }

    private static Task<TempWorkspace> CreateAbstractWorkspaceAsync() =>
        TempWorkspace.CreateAsync(new Dictionary<string, string> { ["Abs.cs"] = AbstractSource });

    private static List<string> SortedFqns(IEnumerable<ImplementationInfo> implementations) =>
        implementations.Select(i => i.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static string[] Sorted(params IEnumerable<string>[] parts) =>
        parts.SelectMany(p => p).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void AbstractFilter_DefaultsToNull()
    {
        var @params = new FindImplementationsParams { SourceFile = "/tmp/x.cs", SymbolName = "IFoo" };
        Assert.Null(@params.AbstractFilter);
    }

    // Interface type target: every implementing type is judged on its own IsAbstract. An abstract
    // class with no abstract members (EmptyAbs, Explicit) counts as abstract; abstract records, nested
    // and generic abstract classes are abstract; sealed classes, structs, classes with virtual or
    // extern members, and the concrete subclasses of an abstract implementer (FastRunner, ConRec) are
    // not. Derived interfaces (IReAbs, IDim) are never returned for a type target.
    [SkippableTheory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_AbstractFilter_InterfaceTypeTarget_JudgesEachImplementingType(bool? abstractFilter)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, "IRunner", abstractFilter);

        var inherited = new[] { "App.Abs.ConRec", "App.Abs.FastRunner" };
        var expected = abstractFilter switch
        {
            true => Sorted(AbstractTypes),
            false => Sorted(DirectConcreteTypes, inherited),
            null => Sorted(AbstractTypes, DirectConcreteTypes, inherited)
        };
        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
        Assert.False(data.Truncated);
    }

    // transitive false drops FastRunner / ConRec (they inherit IRunner from an abstract base) before
    // the abstract filter runs, so false keeps only the direct concrete implementers; abstract
    // implementers all list IRunner directly, so true is unchanged.
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_AbstractFilter_CombinesWithTransitiveFalse(bool abstractFilter)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, "IRunner", abstractFilter,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                Transitive = false
            });

        var expected = abstractFilter ? Sorted(AbstractTypes) : Sorted(DirectConcreteTypes);
        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
    }

    // Interface member target: each implementing member is judged on its own IsAbstract. Abstract
    // implementing members (BaseRunner / AbsRec / NestedAbs / GenAbs<T>.Run) and the re-abstraction
    // in a derived interface (abstract void IRunner.Run() in IReAbs) are abstract; members with bodies
    // in an abstract class (EmptyAbs.Run), explicit implementations (Explicit), the default-body
    // re-implementation in IDim, virtual members, and members of concrete types are not. Overrides of
    // an abstract implementing member (FastRunner.Run, ConRec.Run) are not implementations and are
    // never listed (current behavior).
    [SkippableTheory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_AbstractFilter_InterfaceMethodTarget_JudgesEachImplementingMember(bool? abstractFilter)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, "Run(", abstractFilter);

        var abstractMembers = new[]
        {
            "App.Abs.AbsRec.Run()", "App.Abs.BaseRunner.Run()", "App.Abs.GenAbs<T>.Run()",
            "App.Abs.IReAbs.App.Abs.IRunner.Run()", "App.Abs.Outer.NestedAbs.Run()"
        };
        var concreteMembers = new[]
        {
            "App.Abs.EmptyAbs.Run()", "App.Abs.Explicit.App.Abs.IRunner.Run()", "App.Abs.ExternImpl.Run()",
            "App.Abs.IDim.App.Abs.IRunner.Run()", "App.Abs.SRunner.Run()", "App.Abs.SealedRunner.Run()",
            "App.Abs.Virt.Run()"
        };
        var expected = abstractFilter switch
        {
            true => Sorted(abstractMembers),
            false => Sorted(concreteMembers),
            null => Sorted(abstractMembers, concreteMembers)
        };
        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
        Assert.False(data.Truncated);
        Assert.DoesNotContain(data.Implementations, i => i.FullyQualifiedName.Contains("FastRunner", StringComparison.Ordinal));
        Assert.DoesNotContain(data.Implementations, i => i.FullyQualifiedName.Contains("ConRec", StringComparison.Ordinal));
    }

    // Property and event targets are judged on the implementing property / event itself: only
    // BaseRunner declares them abstract (AbsRec's Speed / Done have bodies even though AbsRec is
    // abstract).
    [SkippableTheory]
    [InlineData("Speed", true, new[] { "App.Abs.BaseRunner.Speed" })]
    [InlineData("Done", true, new[] { "App.Abs.BaseRunner.Done" })]
    [InlineData("Speed", false, new[] { "App.Abs.AbsRec.Speed", "App.Abs.EmptyAbs.Speed", "App.Abs.Explicit.App.Abs.IRunner.Speed", "App.Abs.ExternImpl.Speed", "App.Abs.GenAbs<T>.Speed", "App.Abs.Outer.NestedAbs.Speed", "App.Abs.SRunner.Speed", "App.Abs.SealedRunner.Speed", "App.Abs.Virt.Speed" })]
    [InlineData("Done", false, new[] { "App.Abs.AbsRec.Done", "App.Abs.EmptyAbs.Done", "App.Abs.Explicit.App.Abs.IRunner.Done", "App.Abs.ExternImpl.Done", "App.Abs.GenAbs<T>.Done", "App.Abs.Outer.NestedAbs.Done", "App.Abs.SRunner.Done", "App.Abs.SealedRunner.Done", "App.Abs.Virt.Done" })]
    public async Task FindImplementations_AbstractFilter_PropertyAndEventTargets(
        string member, bool abstractFilter, string[] expected)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, member, abstractFilter);

        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
    }

    // A static abstract interface member is implemented by static members, which are never abstract,
    // so true is empty even when the implementing type (StatImpl) is abstract; the type target IStat
    // still keeps StatImpl for true.
    [SkippableTheory]
    [InlineData("IStat", null, new[] { "App.Abs.StatImpl" })]
    [InlineData("IStat", true, new[] { "App.Abs.StatImpl" })]
    [InlineData("IStat", false, new string[0])]
    [InlineData("Make", null, new[] { "App.Abs.StatImpl.Make()" })]
    [InlineData("Make", true, new string[0])]
    [InlineData("Make", false, new[] { "App.Abs.StatImpl.Make()" })]
    public async Task FindImplementations_AbstractFilter_StaticAbstractInterfaceMember(
        string token, bool? abstractFilter, string[] expected)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 17, token, abstractFilter);

        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
    }

    // Class targets (abstract Animal / BaseRunner, concrete Virt with a virtual member), abstract members with overrides, and an abstract class
    // implementing an interface (BaseRunner and BaseRunner.Run) return no implementations even without
    // a filter (Roslyn FindImplementationsAsync does not report derived classes or overrides). Pins
    // current behavior, not the intended contract. The derived-interface targets IReAbs and IDim are
    // empty only because AbstractSource declares no type implementing them, not because derived
    // interfaces are unsupported: see
    // FindImplementations_AbstractFilter_DerivedInterfaceTarget_JudgesItsImplementers.
    [SkippableTheory]
    [InlineData(20, "Animal")]
    [InlineData(20, "Legs")]
    [InlineData(4, "BaseRunner")]
    [InlineData(14, "Virt")]
    [InlineData(4, "Run(")]
    [InlineData(14, "Run(")]
    [InlineData(12, "IReAbs")]
    [InlineData(13, "IDim")]
    public async Task FindImplementations_AbstractFilter_ClassAndUnimplementedDerivedInterfaceTargets_AreEmpty(int line, string token)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        foreach (var abstractFilter in new bool?[] { null, true, false })
        {
            var data = await RunAbstractFilterAsync(workspace, line, token, abstractFilter);
            Assert.Empty(data.Implementations);
            Assert.Equal(0, data.TotalCount);
            Assert.False(data.Truncated);
        }
    }

    // A derived-interface target is an interface target like any other: IFast : IRunner is
    // implemented by an abstract class (AbsFast), a concrete class (Turbo), and a concrete subclass of
    // the abstract implementer (ConcFast), each judged on its own IsAbstract. The derived interface
    // itself is never returned, and the base-interface implementer Plain is not an IFast implementer.
    [SkippableTheory]
    [InlineData(null, new[] { "App.Der.AbsFast", "App.Der.ConcFast", "App.Der.Turbo" })]
    [InlineData(true, new[] { "App.Der.AbsFast" })]
    [InlineData(false, new[] { "App.Der.ConcFast", "App.Der.Turbo" })]
    public async Task FindImplementations_AbstractFilter_DerivedInterfaceTarget_JudgesItsImplementers(
        bool? abstractFilter, string[] expected)
    {
        const string source = """
            namespace App.Der
            {
                public interface IRunner { void Run(); }
                public interface IFast : IRunner { }
                public abstract class AbsFast : IFast { public abstract void Run(); }
                public class ConcFast : AbsFast { public override void Run() { } }
                public class Turbo : IFast { public void Run() { } }
                public class Plain : IRunner { public void Run() { } }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Der.cs"] = source });
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Der.cs"),
            Line = 4,
            Column = source.Split('\n')[3].IndexOf("IFast", StringComparison.Ordinal) + 1,
            AbstractFilter = abstractFilter
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("App.Der.IFast", result.Data!.FullyQualifiedName);
        Assert.Equal(expected, SortedFqns(result.Data.Implementations));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindImplementations_AbstractFilter_AppliedBeforeMaxResults()
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, "IRunner", true,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                MaxResults = 2
            });

        Assert.Equal(2, data.Implementations.Count);
        Assert.All(data.Implementations, i => Assert.Contains(i.FullyQualifiedName, AbstractTypes));
        Assert.Equal(AbstractTypes.Length, data.TotalCount);
        Assert.True(data.Truncated);

        var exact = await RunAbstractFilterAsync(workspace, 3, "Speed", true,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                MaxResults = 1
            });
        Assert.Single(exact.Implementations);
        Assert.Equal(1, exact.TotalCount);
        Assert.False(exact.Truncated);
    }

    // Combined with the other symbol filters (all must match): kindFilter Record keeps the abstract
    // record for true and the concrete record for false; nameFilter / accessibilityFilter /
    // staticFilter narrow further.
    [SkippableTheory]
    [InlineData(true, "Record", null, null, null, new[] { "App.Abs.AbsRec" })]
    [InlineData(false, "Record", null, null, null, new[] { "App.Abs.ConRec" })]
    [InlineData(false, "Struct", null, null, null, new[] { "App.Abs.SRunner" })]
    [InlineData(true, "Struct", null, null, null, new string[0])]
    [InlineData(true, null, "Abs", null, null, new[] { "App.Abs.AbsRec", "App.Abs.EmptyAbs", "App.Abs.GenAbs<T>", "App.Abs.Outer.NestedAbs" })]
    [InlineData(false, null, "Runner", null, null, new[] { "App.Abs.FastRunner", "App.Abs.SRunner", "App.Abs.SealedRunner" })]
    [InlineData(true, null, null, "public", null, new[] { "App.Abs.AbsRec", "App.Abs.BaseRunner", "App.Abs.EmptyAbs", "App.Abs.Explicit", "App.Abs.GenAbs<T>", "App.Abs.Outer.NestedAbs" })]
    [InlineData(true, null, null, null, true, new string[0])]
    [InlineData(true, null, null, null, false, new[] { "App.Abs.AbsRec", "App.Abs.BaseRunner", "App.Abs.EmptyAbs", "App.Abs.Explicit", "App.Abs.GenAbs<T>", "App.Abs.Outer.NestedAbs" })]
    public async Task FindImplementations_AbstractFilter_CombinesWithSymbolFilters(
        bool abstractFilter, string? kindFilter, string? nameFilter, string? accessibilityFilter, bool? staticFilter,
        string[] expected)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 3, "IRunner", abstractFilter,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                KindFilter = kindFilter,
                NameFilter = nameFilter,
                AccessibilityFilter = accessibilityFilter,
                StaticFilter = staticFilter
            });

        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_AbstractFilter_CombinesWithImplementationFile(bool abstractFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Abs.cs"] = AbstractSource,
            ["More.cs"] = "namespace App.Abs { public sealed class SealedMore : IRunner { public void Run() { } public int Speed => 0; public event System.Action Done { add { } remove { } } } }"
        });

        var data = await RunAbstractFilterAsync(workspace, 3, "IRunner", abstractFilter,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                ImplementationFile = workspace.PathOf("More.cs")
            });

        var fqns = SortedFqns(data.Implementations);
        Assert.Equal(abstractFilter ? Array.Empty<string>() : new[] { "App.Abs.SealedMore" }, fqns);
        Assert.Equal(fqns.Count, data.TotalCount);
    }

    // Implementations in other projects of the solution are returned and judged like local ones.
    [SkippableTheory]
    [InlineData("IRunner", null, new[] { "AbsApp", "L1", "P1", "P2", "P3" })]
    [InlineData("IRunner", true, new[] { "AbsApp" })]
    [InlineData("IRunner", false, new[] { "L1", "P1", "P2", "P3" })]
    [InlineData("Run", true, new[] { "AbsApp.Run()" })]
    [InlineData("Run", false, new[] { "L1.Run()", "P1.Run()", "P2.Run()", "P3.Run()" })]
    public async Task FindImplementations_AbstractFilter_OtherProjects_AreReturnedAndFiltered(
        string symbolName, bool? abstractFilter, string[] expected)
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            extraAppSource: "public abstract class AbsApp : IRunner { public abstract void Run(); }");
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.LibInterfacePath,
            SymbolName = symbolName,
            AbstractFilter = abstractFilter
        });

        Assert.True(result.Success);
        Assert.Equal(
            expected,
            result.Data!.Implementations.Select(i => i.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    // A constructed generic interface target (IGen<int> in IntG's base list or as Holder's field
    // type) resolves to IGen<int> but reports the implementers of every construction of IGen<T>,
    // including AbsStrG : IGen<string> and the open generics OpenG<T> / AbsOpenG<T>, each judged on
    // its own IsAbstract; transitive false keeps the same set.
    private const string GenericInterfaceSource = """
        namespace App.Gen
        {
            public interface IGen<T> { void Put(T t); }
            public class IntG : IGen<int> { public void Put(int t) { } }
            public class OpenG<T> : IGen<T> { public void Put(T t) { } }
            public abstract class AbsOpenG<T> : IGen<T> { public abstract void Put(T t); }
            public abstract class AbsStrG : IGen<string> { public abstract void Put(string t); }
            public class Holder { IGen<int> field; }
        }
        """;

    [SkippableTheory]
    [InlineData(4, null, null, new[] { "App.Gen.AbsOpenG<T>", "App.Gen.AbsStrG", "App.Gen.IntG", "App.Gen.OpenG<T>" })]
    [InlineData(4, true, null, new[] { "App.Gen.AbsOpenG<T>", "App.Gen.AbsStrG" })]
    [InlineData(4, false, null, new[] { "App.Gen.IntG", "App.Gen.OpenG<T>" })]
    [InlineData(8, null, null, new[] { "App.Gen.AbsOpenG<T>", "App.Gen.AbsStrG", "App.Gen.IntG", "App.Gen.OpenG<T>" })]
    [InlineData(8, true, null, new[] { "App.Gen.AbsOpenG<T>", "App.Gen.AbsStrG" })]
    [InlineData(8, false, null, new[] { "App.Gen.IntG", "App.Gen.OpenG<T>" })]
    [InlineData(4, true, false, new[] { "App.Gen.AbsOpenG<T>", "App.Gen.AbsStrG" })]
    [InlineData(4, false, false, new[] { "App.Gen.IntG", "App.Gen.OpenG<T>" })]
    public async Task FindImplementations_AbstractFilter_ConstructedGenericInterfaceTarget_ReportsEveryConstruction(
        int line, bool? abstractFilter, bool? transitive, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Gen.cs"] = GenericInterfaceSource });
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Gen.cs"),
            Line = line,
            Column = GenericInterfaceSource.Split('\n')[line - 1].IndexOf("IGen", StringComparison.Ordinal) + 1,
            AbstractFilter = abstractFilter,
            Transitive = transitive
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("App.Gen.IGen<int>", result.Data!.FullyQualifiedName);
        Assert.Equal(expected, SortedFqns(result.Data.Implementations));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    // staticFilter and abstractFilter must both match (AND). Implementing types are never static, so
    // staticFilter true empties a type target whatever abstractFilter is; the implementation of the
    // static abstract member IStat.Make is static and never abstract, so it survives only staticFilter
    // true with abstractFilter false (or omitted).
    [SkippableTheory]
    [InlineData("IStat", true, null, new string[0])]
    [InlineData("IStat", true, true, new string[0])]
    [InlineData("IStat", false, true, new[] { "App.Abs.StatImpl" })]
    [InlineData("IStat", false, false, new string[0])]
    [InlineData("Make", true, null, new[] { "App.Abs.StatImpl.Make()" })]
    [InlineData("Make", true, false, new[] { "App.Abs.StatImpl.Make()" })]
    [InlineData("Make", true, true, new string[0])]
    [InlineData("Make", false, null, new string[0])]
    [InlineData("Make", false, false, new string[0])]
    public async Task FindImplementations_AbstractFilter_AndsWithStaticFilter_OnStaticAbstractMember(
        string token, bool staticFilter, bool? abstractFilter, string[] expected)
    {
        await using var workspace = await CreateAbstractWorkspaceAsync();

        var data = await RunAbstractFilterAsync(workspace, 17, token, abstractFilter,
            p => new FindImplementationsParams
            {
                SourceFile = p.SourceFile,
                Line = p.Line,
                Column = p.Column,
                AbstractFilter = p.AbstractFilter,
                StaticFilter = staticFilter
            });

        Assert.Equal(expected, SortedFqns(data.Implementations));
        Assert.Equal(expected.Length, data.TotalCount);
    }

    // Implementations declared only in metadata are never listed but, for a metadata target
    // (System.IDisposable), still count toward TotalCount when no location filter applies and are
    // judged on their own IsAbstract (abstract framework types such as System.IO.Stream implement
    // IDisposable), so true lists nothing here yet TotalCount > 0 and Truncated is true. Pins current
    // behavior.
    [SkippableTheory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindImplementations_AbstractFilter_MetadataImplementations_AreCountedButNotListed(bool? abstractFilter)
    {
        const string source = """
            namespace App.Meta
            {
                public class Disp : System.IDisposable { public void Dispose() { } }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(
            new Dictionary<string, string> { ["Meta.cs"] = source });
        var operation = new FindImplementationsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindImplementationsParams
        {
            SourceFile = workspace.PathOf("Meta.cs"),
            Line = 3,
            Column = source.Split('\n')[2].IndexOf("IDisposable", StringComparison.Ordinal) + 1,
            AbstractFilter = abstractFilter
        });

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.Equal("System.IDisposable", data.FullyQualifiedName);
        if (abstractFilter == true)
            Assert.Empty(data.Implementations);
        else
            Assert.Equal("App.Meta.Disp", Assert.Single(data.Implementations).FullyQualifiedName);
        Assert.True(data.TotalCount > data.Implementations.Count);
        Assert.True(data.Truncated);

        if (abstractFilter == null)
        {
            // true and false partition the counted metadata implementations.
            var counts = new List<int>();
            foreach (var filter in new[] { true, false })
            {
                var part = await operation.ExecuteAsync(new FindImplementationsParams
                {
                    SourceFile = workspace.PathOf("Meta.cs"),
                    Line = 3,
                    Column = source.Split('\n')[2].IndexOf("IDisposable", StringComparison.Ordinal) + 1,
                    AbstractFilter = filter
                });
                Assert.True(part.Success);
                Assert.True(part.Data!.TotalCount > 0);
                counts.Add(part.Data.TotalCount);
            }
            Assert.Equal(data.TotalCount, counts.Sum());
        }
    }

    #endregion

    private static List<string> SortedNames(IEnumerable<ImplementationInfo> implementations) =>
        implementations.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    // Roslyn does not guarantee implementation order across runs, so compare sites as sorted sets.
    private static List<(string File, int Line, int Column, string Name)> SortedSites(
        IEnumerable<ImplementationInfo> implementations) =>
        implementations
            .Select(i => (i.File, i.Line, i.Column, i.Name))
            .OrderBy(i => i.File, StringComparer.Ordinal)
            .ThenBy(i => i.Line)
            .ThenBy(i => i.Column)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindImplementations_" + Guid.NewGuid().ToString("N"));
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
        public required string LibInterfacePath { get; init; }
        public required string LibImplPath { get; init; }
        public required string AppAPath { get; init; }
        public required string AppBPath { get; init; }
        public string? LinkedPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null, string? extraAppSource = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindImplementationsXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libInterface = Path.Combine(libDir, "IRunner.cs");
            var libImpl = Path.Combine(libDir, "LibImpl.cs");
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
            await File.WriteAllTextAsync(libInterface, LibInterfaceSource);
            await File.WriteAllTextAsync(libImpl, LibImplSource);
            await File.WriteAllTextAsync(appA, AppASource);
            await File.WriteAllTextAsync(appB, AppBSource);
            // Optional extra App source (App/AppC.cs), picked up by the SDK's default compile glob.
            var appC = Path.Combine(appDir, "AppC.cs");
            if (extraAppSource != null)
                await File.WriteAllTextAsync(appC, extraAppSource);

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
                if (context.GetDocumentByPath(libInterface) == null ||
                    context.GetDocumentByPath(libImpl) == null ||
                    context.GetDocumentByPath(appA) == null ||
                    context.GetDocumentByPath(appB) == null ||
                    (extraAppSource != null && context.GetDocumentByPath(appC) == null))
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibInterfacePath = libInterface,
                    LibImplPath = libImpl,
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

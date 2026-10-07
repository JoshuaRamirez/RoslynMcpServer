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
/// <c>projectPath</c>).
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

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null)
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

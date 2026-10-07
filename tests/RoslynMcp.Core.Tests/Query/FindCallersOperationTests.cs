using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindCallersOperation"/> optional <c>callerFile</c>
/// (restricts reported call sites to callers located in that file; peer of
/// <c>search_symbols</c> / <c>get_diagnostics</c> optional <c>sourceFile</c>), and optional
/// <c>projectPath</c> (restricts reported call sites to one project; peer of <c>get_diagnostics</c> /
/// <c>search_symbols</c> / <c>find_references</c> <c>projectPath</c>).
/// </summary>
public class FindCallersOperationTests
{
    // Target.Run is declared in Target.cs and called twice from CallerA.cs and once from CallerB.cs.
    // Empty.cs has no callers.
    private const string TargetSource = """
        public class Target
        {
            public void Run() { }
        }
        """;

    private const string CallerASource = """
        public class CallerA
        {
            public void First(Target t) { t.Run(); }
            public void Second(Target t) { t.Run(); }
        }
        """;

    private const string CallerBSource = """
        public class CallerB
        {
            public void Third(Target t) { t.Run(); }
        }
        """;

    private const string EmptySource = """
        public class Unrelated
        {
            public void Nothing() { }
        }
        """;

    [Fact]
    public void CallerFile_DefaultsToNull()
    {
        var @params = new FindCallersParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.CallerFile);
    }

    [SkippableFact]
    public async Task FindCallers_OmittedCallerFile_ReturnsCallersFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(3, result.Data.Callers.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal(2, result.Data.Callers.Count(c => SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(1, result.Data.Callers.Count(c => SamePath(c.File, workspace.PathOf("CallerB.cs"))));
    }

    [SkippableFact]
    public async Task FindCallers_CallerFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Callers.Count);
        Assert.All(result.Data.Callers, c => Assert.True(SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(new[] { "First", "Second" }, result.Data.Callers.Select(c => c.CallerName).OrderBy(n => n));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("Run", result.Data.SymbolName);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileOtherFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            Line = 3,
            Column = 17,
            CallerFile = workspace.PathOf("CallerB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Callers);
        Assert.Equal("Third", only.CallerName);
        Assert.True(SamePath(only.File, workspace.PathOf("CallerB.cs")));
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithNoCallers_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Callers);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Callers);
        Assert.True(SamePath(only.File, workspace.PathOf("CallerA.cs")));
        // TotalCount reflects the filtered set (2 in CallerA.cs), not the solution-wide 3.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CallerFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("CallerB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Callers);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_NonexistentCallerFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_RelativeCallerFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = "CallerA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_NonCsCallerFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindCallers_AliasedCallerFilePath_StillMatchesCallSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "CallerA.cs");

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Callers.Count);
        Assert.All(result.Data.Callers, c => Assert.True(SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_CaseOnlyDifferentCallerFile_StillMatchesCallSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        var caseVariant = workspace.PathOf("CALLERA.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to CallerA.cs. On case-sensitive volumes (Linux) callerFile must exist to pass
        // validation (same as search_symbols sourceFile), so create a call-free stub at the
        // case-variant path; the documented OrdinalIgnoreCase comparison must still match
        // CallerA.cs's call sites, which a case-sensitive key comparison would drop.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no callers");

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            CallerFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Callers.Count);
        Assert.All(result.Data.Callers, c => Assert.True(SamePath(c.File, workspace.PathOf("CallerA.cs"))));
        Assert.Equal(new[] { "First", "Second" }, result.Data.Callers.Select(c => c.CallerName).OrderBy(n => n));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #region projectPath

    // Two-project scenario: Lib/Target.cs declares Lib.Target.Run and calls it once (Self, line 6);
    // App (which references Lib) calls it twice in UserA.cs (First line 5, Second line 6) and once in
    // UserB.cs (Third line 5). Solution-wide: 4 call sites.
    private const string LibTargetSource = """
        namespace Lib
        {
            public class Target
            {
                public void Run() { }
                public void Self() { Run(); }
            }
        }
        """;

    private const string AppUserASource = """
        namespace App
        {
            public class UserA
            {
                public void First(Lib.Target t) { t.Run(); }
                public void Second(Lib.Target t) { t.Run(); }
            }
        }
        """;

    private const string AppUserBSource = """
        namespace App
        {
            public class UserB
            {
                public void Third(Lib.Target t) { t.Run(); }
            }
        }
        """;

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new FindCallersParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
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
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ProjectPath = projectPath
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ProjectPath = workspace.PathOf("Target.cs")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathOmittedOrBlank_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run"
        });
        var blank = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = "  "
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(4, omitted.Data!.TotalCount);
        Assert.Equal(4, omitted.Data.Callers.Count);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(1, omitted.Data.Callers.Count(c => SamePath(c.File, workspace.LibTargetPath)));
        Assert.Equal(2, omitted.Data.Callers.Count(c => SamePath(c.File, workspace.AppUserAPath)));
        Assert.Equal(1, omitted.Data.Callers.Count(c => SamePath(c.File, workspace.AppUserBPath)));
        Assert.Equal(SortedCallSites(omitted.Data.Callers), SortedCallSites(blank.Data!.Callers));
        Assert.Equal(omitted.Data.TotalCount, blank.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        // sourceFile (Lib) still locates the symbol; projectPath (App) only scopes reported call sites.
        var app = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal("Run", app.Data!.SymbolName);
        Assert.Equal(3, app.Data.Callers.Count);
        Assert.Equal(3, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);
        Assert.Equal(new[] { "First", "Second", "Third" }, app.Data.Callers.Select(c => c.CallerName).OrderBy(n => n));
        Assert.All(app.Data.Callers, c => Assert.True(
            SamePath(c.File, workspace.AppUserAPath) || SamePath(c.File, workspace.AppUserBPath)));

        var lib = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        var only = Assert.Single(lib.Data!.Callers);
        Assert.Equal("Self", only.CallerName);
        Assert.True(SamePath(only.File, workspace.LibTargetPath));
        Assert.Equal(6, only.Line);
        Assert.Equal(1, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathWithCallerFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var sameProject = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            CallerFile = workspace.AppUserAPath
        });

        Assert.True(sameProject.Success);
        Assert.Equal(2, sameProject.Data!.Callers.Count);
        Assert.All(sameProject.Data.Callers, c => Assert.True(SamePath(c.File, workspace.AppUserAPath)));
        Assert.Equal(new[] { "First", "Second" }, sameProject.Data.Callers.Select(c => c.CallerName).OrderBy(n => n));
        Assert.Equal(2, sameProject.Data.TotalCount);
        Assert.False(sameProject.Data.Truncated);

        var otherProject = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath,
            CallerFile = workspace.AppUserAPath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.Callers);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathWithMaxResults_CapsAfterProjectFilter()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.Callers.Count);
        // TotalCount reflects the project-scoped set (3 in App), not the solution-wide 4.
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.DoesNotContain(capped.Data.Callers, c => SamePath(c.File, workspace.LibTargetPath));

        var notCapped = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 3
        });

        Assert.True(notCapped.Success);
        Assert.Equal(3, notCapped.Data!.Callers.Count);
        Assert.Equal(3, notCapped.Data.TotalCount);
        Assert.False(notCapped.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathWithCallerFileAndMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            CallerFile = workspace.AppUserAPath,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Callers);
        Assert.True(SamePath(only.File, workspace.AppUserAPath));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.AppProjectPath)!, "APP.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.Equal(3, result.Data!.TotalCount);
            Assert.DoesNotContain(result.Data.Callers, c => SamePath(c.File, workspace.LibTargetPath));
        }
        else
        {
            // Case-sensitive volume (Linux): APP.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathLinkedFile_KeepsOnlyThatProjectsDocument()
    {
        // Linked.cs is compiled into both Lib and App, so its call to Run is reported once per
        // project document; projectPath keeps only the copy whose document belongs to that project.
        await using var workspace = await TwoProjectWorkspace.CreateAsync(linkedSource: """
            namespace Shared
            {
                internal class Linker
                {
                    public void Go(Lib.Target t) { t.Run(); }
                }
            }
            """);
        var operation = new FindCallersOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal(4, app.Data!.TotalCount);
        var appLinked = Assert.Single(app.Data.Callers, c => SamePath(c.File, workspace.LinkedPath!));
        Assert.Equal("Go", appLinked.CallerName);
        Assert.Equal(5, appLinked.Line);
        Assert.DoesNotContain(app.Data.Callers, c => SamePath(c.File, workspace.LibTargetPath));

        var lib = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(2, lib.Data!.TotalCount);
        Assert.Single(lib.Data.Callers, c => SamePath(c.File, workspace.LinkedPath!));
        Assert.Single(lib.Data.Callers, c => c.CallerName == "Self");
    }

    [SkippableFact]
    public async Task FindCallers_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync();
        var operation = new FindCallersOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var scoped = await operation.ExecuteAsync(new FindCallersParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(3, scoped.Data!.TotalCount);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(SortedCallSites(omitted.Data.Callers), SortedCallSites(scoped.Data.Callers));
    }

    #endregion

    // Roslyn does not guarantee caller order across runs, so compare call sites as sorted sets.
    private static List<(string File, int Line, int Column, string CallerName)> SortedCallSites(
        IEnumerable<CallerInfo> callers) =>
        callers
            .Select(c => (c.File, c.Line, c.Column, c.CallerName))
            .OrderBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Line)
            .ThenBy(c => c.Column)
            .ThenBy(c => c.CallerName, StringComparer.Ordinal)
            .ToList();

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

        public static async Task<TempWorkspace> CreateAsync()
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindCallers_" + Guid.NewGuid().ToString("N"));
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

            var files = new Dictionary<string, string>
            {
                ["Target.cs"] = TargetSource,
                ["CallerA.cs"] = CallerASource,
                ["CallerB.cs"] = CallerBSource,
                ["Empty.cs"] = EmptySource
            };

            foreach (var (name, source) in files)
                await File.WriteAllTextAsync(Path.Combine(directory, name), source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var name in files.Keys)
                {
                    if (context.GetDocumentByPath(Path.Combine(directory, name)) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {name}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
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
        public required string LibTargetPath { get; init; }
        public required string AppUserAPath { get; init; }
        public required string AppUserBPath { get; init; }
        public string? LinkedPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindCallersXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libTarget = Path.Combine(libDir, "Target.cs");
            var appUserA = Path.Combine(appDir, "UserA.cs");
            var appUserB = Path.Combine(appDir, "UserB.cs");

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
            await File.WriteAllTextAsync(libTarget, LibTargetSource);
            await File.WriteAllTextAsync(appUserA, AppUserASource);
            await File.WriteAllTextAsync(appUserB, AppUserBSource);

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
                if (context.GetDocumentByPath(libTarget) == null ||
                    context.GetDocumentByPath(appUserA) == null ||
                    context.GetDocumentByPath(appUserB) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibTargetPath = libTarget,
                    AppUserAPath = appUserA,
                    AppUserBPath = appUserB,
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

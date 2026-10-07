using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="FindReferencesOperation"/> optional <c>includeDeclaration</c>
/// (LSP <c>ReferenceContext.includeDeclaration</c> peer) and optional <c>referenceFile</c>
/// (restricts reported locations to one file; peer of <c>find_callers</c> <c>callerFile</c>), and
/// optional <c>projectPath</c> (restricts reported locations to one project; peer of
/// <c>get_diagnostics</c> / <c>search_symbols</c> <c>projectPath</c>).
/// </summary>
public class FindReferencesOperationTests
{
    // Three partial parts of Widget (3 definition locations) plus two type usages in Consumer.
    // Usages are parameter types (no object creation) so the implicit constructor does not cascade.
    private const string PartialWidgetSource = """
        partial class Widget { public int A; }
        partial class Widget { public int B; }
        partial class Widget { public int C; }
        class Consumer
        {
            void Use(Widget first, Widget second) { }
        }
        """;

    // Position of "Widget" in the first partial part (line 1, column 15).
    private const int WidgetLine = 1;
    private const int WidgetColumn = 15;

    [Fact]
    public void IncludeDeclaration_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.IncludeDeclaration);
    }

    [SkippableFact]
    public async Task FindReferences_OmittedIncludeDeclaration_IncludesDefinitions()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // Roslyn cascades a type search to its implicit constructor, whose locations are the
        // partial parts too, so assert "at least the three parts" rather than an exact count.
        Assert.True(result.Data.References.Count(r => r.IsDefinition) >= 3);
        Assert.Equal(2, result.Data.References.Count(r => !r.IsDefinition));
        Assert.Equal(result.Data.References.Count, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationTrue_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn
        });
        var explicitTrue = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = true
        });

        Assert.True(omitted.Success);
        Assert.True(explicitTrue.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(explicitTrue.Data);
        Assert.Equal(omitted.Data.TotalCount, explicitTrue.Data.TotalCount);
        Assert.Equal(
            omitted.Data.References.Select(r => (r.File, r.Line, r.Column, r.IsDefinition)),
            explicitTrue.Data.References.Select(r => (r.File, r.Line, r.Column, r.IsDefinition)));
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalse_ReturnsUsagesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.DoesNotContain(result.Data.References, r => r.IsDefinition);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.Equal(6, r.Line));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalseWithMaxResults_CapsUsagesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PartialWidgetSource);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = WidgetLine,
            Column = WidgetColumn,
            IncludeDeclaration = false,
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.References);
        Assert.False(only.IsDefinition);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_IncludeDeclarationFalse_UnusedSymbol_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Lonely
            {
                public void Unused() { }
            }
            """);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Unused",
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    // referenceFile scenario: Target.Run is declared in Target.cs (line 3, column 17) and used once
    // there (Self), twice in UserA.cs and once in UserB.cs. Empty.cs has no references.
    private static readonly Dictionary<string, string> MultiFileSources = new()
    {
        ["Target.cs"] = """
            public class Target
            {
                public void Run() { }
                public void Self() { Run(); }
            }
            """,
        ["UserA.cs"] = """
            public class UserA
            {
                public void First(Target t) { t.Run(); }
                public void Second(Target t) { t.Run(); }
            }
            """,
        ["UserB.cs"] = """
            public class UserB
            {
                public void Third(Target t) { t.Run(); }
            }
            """,
        ["Empty.cs"] = """
            public class Unrelated
            {
                public void Nothing() { }
            }
            """
    };

    [Fact]
    public void ReferenceFile_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.ReferenceFile);
    }

    [SkippableFact]
    public async Task FindReferences_OmittedReferenceFile_ReturnsLocationsFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(5, result.Data.References.Count);
        Assert.Equal(5, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        var definition = Assert.Single(result.Data.References, r => r.IsDefinition);
        Assert.True(SamePath(definition.File, workspace.PathOf("Target.cs")));
        Assert.Equal(1, result.Data.References.Count(r => !r.IsDefinition && SamePath(r.File, workspace.PathOf("Target.cs"))));
        Assert.Equal(2, result.Data.References.Count(r => SamePath(r.File, workspace.PathOf("UserA.cs"))));
        Assert.Equal(1, result.Data.References.Count(r => SamePath(r.File, workspace.PathOf("UserB.cs"))));
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("UserA.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.True(SamePath(r.File, workspace.PathOf("UserA.cs"))));
        // The declaration lives in Target.cs, so it is filtered out even with includeDeclaration on.
        Assert.DoesNotContain(result.Data.References, r => r.IsDefinition);
        Assert.Equal(new[] { 3, 4 }, result.Data.References.Select(r => r.Line).OrderBy(l => l));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("Run", result.Data.SymbolName);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileOtherFile_LineColumnResolution_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            Line = 3,
            Column = 17,
            ReferenceFile = workspace.PathOf("UserB.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.References);
        Assert.False(only.IsDefinition);
        Assert.True(SamePath(only.File, workspace.PathOf("UserB.cs")));
        Assert.Equal(3, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileIsDeclaringFile_IncludesDeclarationAndLocalUsage()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("Target.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.True(SamePath(r.File, workspace.PathOf("Target.cs"))));
        var definition = Assert.Single(result.Data.References, r => r.IsDefinition);
        Assert.Equal(3, definition.Line);
        var usage = Assert.Single(result.Data.References, r => !r.IsDefinition);
        Assert.Equal(4, usage.Line);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileWithIncludeDeclarationFalse_ReturnsUsagesInThatFileOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("Target.cs"),
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.References);
        Assert.False(only.IsDefinition);
        Assert.Equal(4, only.Line);
        Assert.True(SamePath(only.File, workspace.PathOf("Target.cs")));
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileWithNoReferences_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("UserA.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.References);
        Assert.True(SamePath(only.File, workspace.PathOf("UserA.cs")));
        // TotalCount reflects the filtered set (2 in UserA.cs), not the solution-wide 5.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ReferenceFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("UserB.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.References);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NonexistentReferenceFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindReferences_RelativeReferenceFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = "UserA.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindReferences_NonCsReferenceFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task FindReferences_AliasedReferenceFilePath_StillMatchesLocations()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "UserA.cs");

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.True(SamePath(r.File, workspace.PathOf("UserA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_CaseOnlyDifferentReferenceFile_StillMatchesLocations()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);
        var caseVariant = workspace.PathOf("USERA.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to UserA.cs. On case-sensitive volumes (Linux) referenceFile must exist to pass
        // validation (same as find_callers callerFile), so create a reference-free stub at the
        // case-variant path; the documented OrdinalIgnoreCase comparison must still match
        // UserA.cs's references, which a case-sensitive key comparison would drop.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no references");

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.References.Count);
        Assert.All(result.Data.References, r => Assert.True(SamePath(r.File, workspace.PathOf("UserA.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #region projectPath

    // Two-project scenario: Lib/Target.cs declares Lib.Target.Run (line 5) and uses it once (Self,
    // line 6); App (which references Lib) uses it twice in UserA.cs (lines 5, 6) and once in UserB.cs
    // (line 5). Solution-wide: 1 definition + 4 usages.
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
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
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
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
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
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ProjectPath = workspace.PathOf("Target.cs")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathOmittedOrBlank_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run"
        });
        var blank = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = "  "
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(5, omitted.Data!.TotalCount);
        Assert.Equal(5, omitted.Data.References.Count);
        Assert.False(omitted.Data.Truncated);
        Assert.Single(omitted.Data.References, r => r.IsDefinition);
        Assert.Equal(2, omitted.Data.References.Count(r => SamePath(r.File, workspace.LibTargetPath)));
        Assert.Equal(2, omitted.Data.References.Count(r => SamePath(r.File, workspace.AppUserAPath)));
        Assert.Equal(1, omitted.Data.References.Count(r => SamePath(r.File, workspace.AppUserBPath)));
        Assert.Equal(
            SortedLocations(omitted.Data.References),
            SortedLocations(blank.Data!.References));
        Assert.Equal(omitted.Data.TotalCount, blank.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        // sourceFile (Lib) still locates the symbol; projectPath (App) only scopes reported locations.
        var app = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal("Run", app.Data!.SymbolName);
        Assert.Equal(3, app.Data.References.Count);
        Assert.Equal(3, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);
        // The declaration lives in Lib, so it is filtered out even with includeDeclaration on.
        Assert.DoesNotContain(app.Data.References, r => r.IsDefinition);
        Assert.All(app.Data.References, r => Assert.True(
            SamePath(r.File, workspace.AppUserAPath) || SamePath(r.File, workspace.AppUserBPath)));

        var lib = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(2, lib.Data!.References.Count);
        Assert.Equal(2, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);
        Assert.All(lib.Data.References, r => Assert.True(SamePath(r.File, workspace.LibTargetPath)));
        var definition = Assert.Single(lib.Data.References, r => r.IsDefinition);
        Assert.Equal(5, definition.Line);
        var usage = Assert.Single(lib.Data.References, r => !r.IsDefinition);
        Assert.Equal(6, usage.Line);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathWithIncludeDeclarationFalse_ReturnsProjectUsagesOnly()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath,
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.References);
        Assert.False(only.IsDefinition);
        Assert.Equal(6, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathWithReferenceFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var sameProject = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            ReferenceFile = workspace.AppUserAPath
        });

        Assert.True(sameProject.Success);
        Assert.Equal(2, sameProject.Data!.References.Count);
        Assert.All(sameProject.Data.References, r => Assert.True(SamePath(r.File, workspace.AppUserAPath)));
        Assert.Equal(new[] { 5, 6 }, sameProject.Data.References.Select(r => r.Line).OrderBy(l => l));
        Assert.Equal(2, sameProject.Data.TotalCount);
        Assert.False(sameProject.Data.Truncated);

        var otherProject = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath,
            ReferenceFile = workspace.AppUserAPath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.References);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathWithMaxResults_CapsAfterProjectFilter()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.References.Count);
        // TotalCount reflects the project-scoped set (3 in App), not the solution-wide 5.
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.DoesNotContain(capped.Data.References, r => SamePath(r.File, workspace.LibTargetPath));

        var notCapped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 3
        });

        Assert.True(notCapped.Success);
        Assert.Equal(3, notCapped.Data!.References.Count);
        Assert.Equal(3, notCapped.Data.TotalCount);
        Assert.False(notCapped.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathWithReferenceFileAndMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath,
            ReferenceFile = workspace.AppUserAPath,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.References);
        Assert.True(SamePath(only.File, workspace.AppUserAPath));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.AppProjectPath)!, "APP.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new FindReferencesParams
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
            Assert.DoesNotContain(result.Data.References, r => r.IsDefinition);
        }
        else
        {
            // Case-sensitive volume (Linux): APP.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathLinkedFile_KeepsOnlyThatProjectsDocument()
    {
        // Linked.cs is compiled into both Lib and App, so its usage of Run is reported once per
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
        var operation = new FindReferencesOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal(4, app.Data!.TotalCount);
        var appLinked = Assert.Single(app.Data.References, r => SamePath(r.File, workspace.LinkedPath!));
        Assert.Equal(5, appLinked.Line);
        Assert.DoesNotContain(app.Data.References, r => r.IsDefinition);

        var lib = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(3, lib.Data!.TotalCount);
        Assert.Single(lib.Data.References, r => SamePath(r.File, workspace.LinkedPath!));
        Assert.Single(lib.Data.References, r => r.IsDefinition);
    }

    [SkippableFact]
    public async Task FindReferences_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var scoped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(5, scoped.Data!.TotalCount);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(
            SortedLocations(omitted.Data.References),
            SortedLocations(scoped.Data.References));
    }

    #endregion

    // Roslyn does not guarantee reference order across runs, so compare locations as sorted sets.
    private static List<(string File, int Line, int Column, bool IsDefinition)> SortedLocations(
        IEnumerable<ReferenceLocationInfo> references) =>
        references
            .Select(r => (r.File, r.Line, r.Column, r.IsDefinition))
            .OrderBy(l => l.File, StringComparer.Ordinal)
            .ThenBy(l => l.Line)
            .ThenBy(l => l.Column)
            .ThenBy(l => l.IsDefinition)
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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindReferences_" + Guid.NewGuid().ToString("N"));
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
        public required string LibTargetPath { get; init; }
        public required string AppUserAPath { get; init; }
        public required string AppUserBPath { get; init; }
        public string? LinkedPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpFindReferencesXP_" + Guid.NewGuid().ToString("N"));
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

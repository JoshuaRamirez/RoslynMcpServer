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
/// <c>get_diagnostics</c> / <c>search_symbols</c> <c>projectPath</c>), and optional
/// <c>accessibilityFilter</c> (restricts by reference-site enclosing symbol DeclaredAccessibility; peer
/// of <c>find_callers</c> / <c>find_implementations</c> / <c>search_symbols</c> <c>accessibilityFilter</c>),
/// and optional <c>nameFilter</c> (restricts by reference-site enclosing symbol simple name; peer of
/// <c>get_document_outline</c> / <c>get_symbol_info</c> / <c>find_implementations</c> /
/// <c>get_type_hierarchy</c> <c>nameFilter</c>), and optional <c>kindFilter</c> (restricts by
/// reference-site enclosing symbol kind; peer of <c>find_callers</c> / <c>search_symbols</c> /
/// <c>find_implementations</c> / <c>get_type_hierarchy</c> <c>kindFilter</c>).
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


    #region namespaceFilter

    // namespaceFilter scenario (one project): Target.Run is declared in App.Core (Target.cs line 5) and
    // used once there (Self, line 6); Services.cs uses it in block namespace App.Services (line 5) and,
    // inside a lambda, in nested block namespace App.Services.Orders (line 12); Extra.cs uses it in
    // file-scoped App.ServicesExtra (line 5, prefix but not nested); Lower.cs in app.services (line 5,
    // differs only by case); Global.cs in the global namespace (line 3). Solution-wide: 1 definition +
    // 6 usages.
    private static readonly Dictionary<string, string> NamespaceSources = new()
    {
        ["Target.cs"] = """
            namespace App.Core
            {
                public class Target
                {
                    public void Run() { }
                    public void Self() { Run(); }
                }
            }
            """,
        ["Services.cs"] = """
            namespace App.Services
            {
                public class OrderService
                {
                    public void Place(App.Core.Target t) { t.Run(); }
                }
            }
            namespace App.Services.Orders
            {
                public class OrderHandler
                {
                    public void Handle(App.Core.Target t) { System.Action a = () => t.Run(); a(); }
                }
            }
            """,
        ["Extra.cs"] = """
            namespace App.ServicesExtra;

            public class Extra
            {
                public void Go(App.Core.Target t) { t.Run(); }
            }
            """,
        ["Lower.cs"] = """
            namespace app.services
            {
                public class Lower
                {
                    public void Go(App.Core.Target t) { t.Run(); }
                }
            }
            """,
        ["Global.cs"] = """
            public class GlobalUser
            {
                public void Go(App.Core.Target t) { t.Run(); }
            }
            """
    };

    private static List<(string File, int Line)> FileLines(IEnumerable<ReferenceLocationInfo> references) =>
        references
            .Select(r => (Path.GetFileName(r.File), r.Line))
            .OrderBy(l => l.Item1, StringComparer.Ordinal)
            .ThenBy(l => l.Line)
            .ToList();

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public async Task FindReferences_NamespaceFilterBlankOrRoot_MatchesOmitted(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var filtered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(7, omitted.Data!.TotalCount);
        Assert.Equal(7, filtered.Data!.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal(SortedLocations(omitted.Data.References), SortedLocations(filtered.Data.References));
    }

    [SkippableTheory]
    [InlineData("App.Services")]
    [InlineData("  App.Services  ")]
    [InlineData("global::App.Services")]
    [InlineData(" global::App.Services ")]
    public async Task FindReferences_NamespaceFilter_KeepsSitesInEqualOrNestedNamespace(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        // sourceFile (App.Core) still locates the symbol; the filter scopes reference sites only.
        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Equal("Run", result.Data!.SymbolName);
        // App.Services (line 5) + nested App.Services.Orders lambda (line 12); not App.ServicesExtra,
        // app.services, the global namespace, or the App.Core declaration / Self usage.
        Assert.Equal(
            new List<(string, int)> { ("Services.cs", 5), ("Services.cs", 12) },
            FileLines(result.Data.References));
        Assert.DoesNotContain(result.Data.References, r => r.IsDefinition);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterNestedBlock_KeepsOnlyInnerNamespaceSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        // Two block namespaces in one file: only the lambda in App.Services.Orders matches.
        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.References);
        Assert.True(SamePath(only.File, workspace.PathOf("Services.cs")));
        Assert.Equal(12, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterRoot_IncludesDefinitionAndNestedButNotGlobalOrOtherCase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        Assert.Equal(
            new List<(string, int)>
            {
                ("Extra.cs", 5), ("Services.cs", 5), ("Services.cs", 12), ("Target.cs", 5), ("Target.cs", 6)
            },
            FileLines(result.Data!.References));
        var definition = Assert.Single(result.Data.References, r => r.IsDefinition);
        Assert.Equal(5, definition.Line);
        Assert.Equal(5, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.ServicesExtra", "Extra.cs", 5)]
    [InlineData("app.services", "Lower.cs", 5)]
    public async Task FindReferences_NamespaceFilter_IsOrdinalAndHonorsFileScopedNamespaces(
        string namespaceFilter, string expectedFile, int expectedLine)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.References);
        Assert.True(SamePath(only.File, workspace.PathOf(expectedFile)));
        Assert.Equal(expectedLine, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("App.Serv")]
    [InlineData("App.Services.Orders.Deep")]
    [InlineData("Nope")]
    [InlineData("APP")]
    public async Task FindReferences_NamespaceFilterNoMatch_ReturnsEmptySuccess(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterWithIncludeDeclarationFalse_DropsDefinition()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var withDeclaration = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Core"
        });

        Assert.True(withDeclaration.Success);
        Assert.Equal(2, withDeclaration.Data!.TotalCount);
        Assert.Single(withDeclaration.Data.References, r => r.IsDefinition);

        var usagesOnly = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Core",
            IncludeDeclaration = false
        });

        Assert.True(usagesOnly.Success);
        var only = Assert.Single(usagesOnly.Data!.References);
        Assert.False(only.IsDefinition);
        Assert.Equal(6, only.Line);
        Assert.Equal(1, usagesOnly.Data.TotalCount);
        Assert.False(usagesOnly.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterWithReferenceFile_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var sameFile = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App",
            ReferenceFile = workspace.PathOf("Services.cs")
        });

        Assert.True(sameFile.Success);
        Assert.Equal(
            new List<(string, int)> { ("Services.cs", 5), ("Services.cs", 12) },
            FileLines(sameFile.Data!.References));
        Assert.Equal(2, sameFile.Data.TotalCount);

        var otherFile = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Services",
            ReferenceFile = workspace.PathOf("Extra.cs")
        });

        Assert.True(otherFile.Success);
        Assert.Empty(otherFile.Data!.References);
        Assert.Equal(0, otherFile.Data.TotalCount);
        Assert.False(otherFile.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterWithMaxResults_CapsAfterNamespaceFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.References.Count);
        // TotalCount reflects the namespace-filtered set (5 under App), not the solution-wide 7.
        Assert.Equal(5, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.DoesNotContain(capped.Data.References, r =>
            SamePath(r.File, workspace.PathOf("Global.cs")) || SamePath(r.File, workspace.PathOf("Lower.cs")));

        var notCapped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = "App.Services",
            MaxResults = 2
        });

        Assert.True(notCapped.Success);
        Assert.Equal(2, notCapped.Data!.References.Count);
        Assert.Equal(2, notCapped.Data.TotalCount);
        Assert.False(notCapped.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterTypeTarget_AttributesUsingAliasAndBaseListToEnclosingNamespace()
    {
        // A using alias outside any namespace is a global-namespace site; the alias and base list
        // inside App.Services.Aliases belong to that namespace (the enclosing symbol is the namespace
        // or the declaring type, not the target).
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App.Core
                {
                    public class Target { }
                }
                """,
            ["Aliases.cs"] = """
                using GlobalAlias = App.Core.Target;

                namespace App.Services.Aliases
                {
                    using LocalAlias = App.Core.Target;

                    public class Derived : App.Core.Target { }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var scoped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            NamespaceFilter = "App.Services"
        });

        Assert.True(scoped.Success);
        Assert.Equal(
            new List<(string, int)> { ("Aliases.cs", 5), ("Aliases.cs", 7) },
            FileLines(scoped.Data!.References));
        Assert.Equal(2, scoped.Data.TotalCount);

        var core = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            NamespaceFilter = "App.Core"
        });

        // Only the declaration site(s) in App.Core remain (Roslyn also reports the implicit
        // constructor's definition at the class declaration); no usage lives in App.Core.
        Assert.True(core.Success);
        Assert.NotEmpty(core.Data!.References);
        Assert.All(core.Data.References, r =>
        {
            Assert.True(r.IsDefinition);
            Assert.True(SamePath(r.File, workspace.PathOf("Target.cs")));
            Assert.Equal(3, r.Line);
        });
        Assert.Equal(core.Data.References.Count, core.Data.TotalCount);

        var global = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target"
        });

        // Without the filter the global-namespace alias (line 1) is reported too.
        Assert.True(global.Success);
        Assert.Contains(global.Data!.References, r => SamePath(r.File, workspace.PathOf("Aliases.cs")) && r.Line == 1);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilter_KeepsSourceGeneratedDefinitions()
    {
        // [GeneratedRegex] makes the BCL regex source generator emit a second `partial class Rx`
        // part (in the same namespace) into a generated tree that is not a regular document; its
        // definition location must still be attributed to App.Gen rather than dropped.
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text.RegularExpressions;
            namespace App.Gen
            {
                public static partial class Rx
                {
                    [GeneratedRegex("ab+")]
                    public static partial Regex Ab();
                }
            }
            """, "Rx.cs");
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Rx"
        });

        Assert.True(omitted.Success);
        Skip.If(
            omitted.Data!.References.All(r => SamePath(r.File, workspace.SourcePath)),
            "The regex source generator did not run in this MSBuild workspace.");

        var scoped = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Rx",
            NamespaceFilter = "App.Gen"
        });

        Assert.True(scoped.Success);
        Assert.Equal(omitted.Data.TotalCount, scoped.Data!.TotalCount);
        Assert.Equal(SortedLocations(omitted.Data.References), SortedLocations(scoped.Data.References));
        Assert.Contains(scoped.Data.References, r => r.IsDefinition && !SamePath(r.File, workspace.SourcePath));

        var other = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Rx",
            NamespaceFilter = "App.Other"
        });

        Assert.True(other.Success);
        Assert.Empty(other.Data!.References);
        Assert.Equal(0, other.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_NamespaceFilterWithProjectPath_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new FindReferencesOperation(workspace.Context);

        var lib = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            NamespaceFilter = "Lib",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(2, lib.Data!.TotalCount);
        Assert.Single(lib.Data.References, r => r.IsDefinition);

        var app = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            NamespaceFilter = "App"
        });

        Assert.True(app.Success);
        Assert.Equal(3, app.Data!.TotalCount);
        Assert.All(app.Data.References, r => Assert.True(
            SamePath(r.File, workspace.AppUserAPath) || SamePath(r.File, workspace.AppUserBPath)));

        var mismatch = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.LibTargetPath,
            SymbolName = "Run",
            NamespaceFilter = "Lib",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(mismatch.Success);
        Assert.Empty(mismatch.Data!.References);
        Assert.Equal(0, mismatch.Data.TotalCount);
        Assert.False(mismatch.Data.Truncated);
    }

    #endregion

    #region AccessibilityFilter

    // Run is internal on a public type: its definition location is judged by Run itself (internal, not
    // the public containing type) while every usage site is judged by the member enclosing it (lambdas
    // and local functions looked through to their declaring member), never by the target symbol.
    private static readonly Dictionary<string, string> AccessibilitySources = new()
    {
        ["Target.cs"] = """
            namespace App;
            public class Target
            {
                internal void Run() { }
            }
            """,
        ["Host.cs"] = """
            namespace App;
            public class Host
            {
                public void Pub(Target t) { t.Run(); }
                internal void Int(Target t) { t.Run(); }
                protected void Prot(Target t) { t.Run(); }
                private void Priv(Target t) { t.Run(); }
                protected internal void ProtInt(Target t) { t.Run(); }
                private protected void PrivProt(Target t) { t.Run(); }
                void DefaultPriv(Target t) { t.Run(); }
                public void Lam(Target t) { System.Action a = () => t.Run(); a(); }
                internal void Loc(Target t) { void Inner() => t.Run(); Inner(); }
            }
            """,
        ["Services.cs"] = """
            namespace App.Services;
            public class ServiceCaller
            {
                public void Place(App.Target t) { t.Run(); }
                private void Hidden(App.Target t) { t.Run(); }
            }
            """,
        ["OtherFile.cs"] = """
            namespace App;
            public class OtherHost
            {
                public void Elsewhere(Target t) { t.Run(); }
                internal void ElsewhereInt(Target t) { t.Run(); }
            }
            """
    };

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindReferences_BlankAccessibilityFilter_MatchesOmitted(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var filtered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(14, omitted.Data!.TotalCount);
        Assert.Equal(14, filtered.Data!.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal(SortedLocations(omitted.Data.References), SortedLocations(filtered.Data.References));
    }

    [SkippableTheory]
    [InlineData("public", new[] { "Host.cs:4", "Host.cs:11", "OtherFile.cs:4", "Services.cs:4" })]
    [InlineData("internal", new[] { "Host.cs:5", "Host.cs:12", "OtherFile.cs:5", "Target.cs:4" })]
    [InlineData("protected", new[] { "Host.cs:6" })]
    [InlineData("private", new[] { "Host.cs:7", "Host.cs:10", "Services.cs:5" })]
    [InlineData("protected internal", new[] { "Host.cs:8" })]
    [InlineData("private protected", new[] { "Host.cs:9" })]
    public async Task FindReferences_AccessibilityFilter_SelectsSitesByEnclosingSymbolDeclaredAccessibility(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success, accessibilityFilter);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilter_JudgesDefinitionByItsEnclosingSymbolNotTheType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var internalSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "internal"
        });
        var publicSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "public"
        });

        Assert.True(internalSites.Success);
        var definition = Assert.Single(internalSites.Data!.References, r => r.IsDefinition);
        Assert.True(SamePath(definition.File, workspace.PathOf("Target.cs")));
        Assert.Equal(4, definition.Line);

        // The containing type Target is public, but the definition site is judged by Run (internal).
        Assert.True(publicSites.Success);
        Assert.DoesNotContain(publicSites.Data!.References, r => r.IsDefinition);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilter_TopLevelStatementSitesNeverMatch()
    {
        // References in top-level statements (including a top-level local function and lambda) are
        // enclosed by the synthesized private `<Main>$` entry point; they are namespace-level sites and
        // never match a named filter (not even private).
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                public static class Target
                {
                    public static void Run() { }
                }
                """,
            ["Program.cs"] = """
                Target.Run();
                void Local() { Target.Run(); }
                System.Action a = () => Target.Run();
                """,
            ["Host.cs"] = """
                public class Host
                {
                    private void Priv() { Target.Run(); }
                }
                """
        }, "Target.cs");
        var operation = new FindReferencesOperation(workspace.Context);

        var all = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var privateSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "private"
        });
        var internalSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "internal"
        });

        Assert.True(all.Success);
        Assert.Equal(3, all.Data!.References.Count(r => Path.GetFileName(r.File) == "Program.cs"));
        Assert.True(privateSites.Success);
        Assert.Equal(new[] { "Host.cs:3" }, Sites(privateSites.Data!.References.Where(r => !r.IsDefinition)));
        Assert.True(internalSites.Success);
        Assert.DoesNotContain(internalSites.Data!.References, r => Path.GetFileName(r.File) == "Program.cs");
    }

    [SkippableTheory]
    [InlineData("PUBLIC", "public")]
    [InlineData("  Private  ", "private")]
    [InlineData("Protected   Internal", "protected internal")]
    [InlineData("internal protected", "protected internal")]
    [InlineData("protected private", "private protected")]
    public async Task FindReferences_AccessibilityFilter_IsCaseInsensitiveAndAcceptsEitherModifierOrder(
        string accessibilityFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = accessibilityFilter
        });
        var expected = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.References);
        Assert.Equal(Sites(expected.Data.References), Sites(filtered.Data!.References));
    }

    [SkippableTheory]
    [InlineData("friend")]
    [InlineData("ProtectedOrInternal")]
    [InlineData("public, internal")]
    public async Task FindReferences_UnknownAccessibilityFilter_ThrowsInvalidVisibility(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FindReferencesParams
            {
                SourceFile = workspace.PathOf("Target.cs"),
                SymbolName = "Run",
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.StartsWith($"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterWithReferenceFile_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var inHost = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "public",
            ReferenceFile = workspace.PathOf("Host.cs")
        });
        var inTarget = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "internal",
            ReferenceFile = workspace.PathOf("Target.cs")
        });

        Assert.True(inHost.Success);
        Assert.Equal(new[] { "Host.cs:4", "Host.cs:11" }, Sites(inHost.Data!.References));
        Assert.Equal(2, inHost.Data.TotalCount);
        Assert.False(inHost.Data.Truncated);

        Assert.True(inTarget.Success);
        var definition = Assert.Single(inTarget.Data!.References);
        Assert.True(definition.IsDefinition);
        Assert.Equal(1, inTarget.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "public",
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Services.cs:4" }, Sites(result.Data!.References));
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterWithIncludeDeclarationFalse_DropsDefinition()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "internal",
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Host.cs:5", "Host.cs:12", "OtherFile.cs:5" }, Sites(result.Data!.References));
        Assert.All(result.Data.References, r => Assert.False(r.IsDefinition));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterWithMaxResults_CapsAfterAccessibilityFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "public",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.References.Count);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(Sites(result.Data.References), site =>
            Assert.Contains(site, new[] { "Host.cs:4", "Host.cs:11", "OtherFile.cs:4", "Services.cs:4" }));
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = "public class Target { public void Run() { } }",
            ["OnlyPublic.cs"] = "public class OnlyPub { public void Go(Target t) { t.Run(); } }"
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterWithProjectPath_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = "private",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Host.cs:7", "Host.cs:10", "Services.cs:5" }, Sites(result.Data!.References));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterLocalTarget_JudgesDefinitionAndUsagesByDeclaringMember()
    {
        // "total" is a local of the private Compute method (column 33 on line 3).
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class Holder
            {
                private int Compute() { var total = 1; return total + total; }
                public int Expose() => 0;
            }
            """);
        var operation = new FindReferencesOperation(workspace.Context);

        var privateSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = 3,
            Column = 33,
            AccessibilityFilter = "private"
        });
        var publicSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = 3,
            Column = 33,
            AccessibilityFilter = "public"
        });

        Assert.True(privateSites.Success);
        Assert.Equal("total", privateSites.Data!.SymbolName);
        Assert.Equal(3, privateSites.Data.TotalCount);
        Assert.Single(privateSites.Data.References, r => r.IsDefinition);

        Assert.True(publicSites.Success);
        Assert.Empty(publicSites.Data!.References);
        Assert.Equal(0, publicSites.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilter_NamespaceLevelSitesNeverMatch()
    {
        // Roslyn reports INamespaceSymbol.DeclaredAccessibility as Public, so the using alias sites
        // (enclosed by the global namespace and by namespace App.Consumers) must still be excluded.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                using GlobalAlias = App.Target;
                namespace App.Consumers
                {
                    using InnerAlias = App.Target;
                    public class Consumer
                    {
                        public App.Target Make() => null!;
                    }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var unfiltered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false
        });
        var publicSites = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            AccessibilityFilter = "public"
        });

        Assert.True(unfiltered.Success);
        Assert.Equal(new[] { "Consumer.cs:1", "Consumer.cs:4", "Consumer.cs:7" }, Sites(unfiltered.Data!.References));

        Assert.True(publicSites.Success);
        Assert.Equal(new[] { "Consumer.cs:7" }, Sites(publicSites.Data!.References));
        Assert.Equal(1, publicSites.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_AccessibilityFilterNamespaceTarget_DefinitionNeverMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App.Things
            {
                public class Thing { }
            }
            """);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 16,
            AccessibilityFilter = "public"
        });

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Data!.References, r => r.IsDefinition);
    }

    private static List<string> Sites(IEnumerable<ReferenceLocationInfo> references) =>
        references
            .Select(r => (File: Path.GetFileName(r.File), r.Line))
            .OrderBy(l => l.File, StringComparer.Ordinal)
            .ThenBy(l => l.Line)
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

    #endregion

    #region NameFilter

    // Run is internal on a public type: its definition location is judged by Run itself, while every
    // usage site is judged by the member enclosing it (lambdas and local functions looked through to
    // their declaring member, accessors by their own get_X name), never by the target symbol or by the
    // containing type of the enclosing member (OrderHandler.Audit is an "Audit" site, not a "Handle" one).
    private static readonly Dictionary<string, string> NameSources = new()
    {
        ["Target.cs"] = """
            namespace App;
            public class Target
            {
                internal void Run() { }
            }
            """,
        ["Handlers.cs"] = """
            namespace App.Handlers;
            public class OrderHandler
            {
                public void Handle(App.Target t) { t.Run(); }
                private void Audit(App.Target t) { System.Action a = () => t.Run(); a(); }
            }
            public class Reporter
            {
                public void HandleReport(App.Target t) { t.Run(); }
                public int Total { get { new App.Target().Run(); return 0; } }
            }
            """,
        ["Other.cs"] = """
            namespace App;
            public class Consumer
            {
                public void Go(Target t) { t.Run(); }
                private void Helper(Target t) { void Inner() => t.Run(); Inner(); }
            }
            """
    };

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NameFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindReferences_BlankNameFilter_MatchesOmitted(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run"
        });
        var filtered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = nameFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(7, omitted.Data!.TotalCount);
        Assert.Equal(7, filtered.Data!.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal(SortedLocations(omitted.Data.References), SortedLocations(filtered.Data.References));
    }

    [SkippableTheory]
    [InlineData("Handle", new[] { "Handlers.cs:4", "Handlers.cs:9" })]
    [InlineData("handle", new[] { "Handlers.cs:4", "Handlers.cs:9" })]
    [InlineData("  HANDLE  ", new[] { "Handlers.cs:4", "Handlers.cs:9" })]
    [InlineData("Report", new[] { "Handlers.cs:9" })]
    [InlineData("Audit", new[] { "Handlers.cs:5" })]
    [InlineData("Helper", new[] { "Other.cs:5" })]
    [InlineData("Total", new[] { "Handlers.cs:10" })]
    [InlineData("get_", new[] { "Handlers.cs:10" })]
    [InlineData("Go", new[] { "Other.cs:4" })]
    [InlineData("Run", new[] { "Target.cs:4" })]
    [InlineData("H", new[] { "Handlers.cs:4", "Handlers.cs:9", "Other.cs:5" })]
    public async Task FindReferences_NameFilter_SelectsSitesByEnclosingSymbolName(string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = nameFilter
        });

        Assert.True(result.Success, nameFilter);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Inner")]
    [InlineData("OrderHandler")]
    [InlineData("Target")]
    [InlineData("Han*")]
    [InlineData("Hand le")]
    [InlineData("NoSuchName")]
    public async Task FindReferences_NameFilterNoMatch_ReturnsEmptySuccess(string nameFilter)
    {
        // Local functions are looked through to their declaring member (Inner -> Helper), usages are
        // judged by their enclosing member rather than its containing type (OrderHandler) or the target
        // symbol's type (Target), and the value is a literal substring (no glob, inner whitespace kept).
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = nameFilter
        });

        Assert.True(result.Success, nameFilter);
        Assert.Empty(result.Data!.References);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_NameFilter_JudgesDefinitionByDeclaredSymbol()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var withDeclaration = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = "run"
        });
        var usagesOnly = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = "run",
            IncludeDeclaration = false
        });

        Assert.True(withDeclaration.Success);
        var definition = Assert.Single(withDeclaration.Data!.References);
        Assert.True(definition.IsDefinition);
        Assert.True(SamePath(definition.File, workspace.PathOf("Target.cs")));
        Assert.Equal(4, definition.Line);

        // No usage site is enclosed by a member whose name contains "run".
        Assert.True(usagesOnly.Success);
        Assert.Empty(usagesOnly.Data!.References);
        Assert.Equal(0, usagesOnly.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_NameFilterWithMaxResults_CapsAfterNameFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = "H",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.References.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(result.Data.References, r => Assert.Contains(
            $"{Path.GetFileName(r.File)}:{r.Line}", new[] { "Handlers.cs:4", "Handlers.cs:9", "Other.cs:5" }));
    }

    [SkippableTheory]
    [InlineData("public", "Handle", new[] { "Handlers.cs:4", "Handlers.cs:9" })]
    [InlineData("private", "Handle", new string[0])]
    [InlineData("private", "H", new[] { "Other.cs:5" })]
    [InlineData("public", "Total", new[] { "Handlers.cs:10" })]
    [InlineData("internal", "Run", new[] { "Target.cs:4" })]
    [InlineData("public", "Run", new string[0])]
    public async Task FindReferences_NameFilterWithAccessibilityFilter_AndsTheSiteFilters(
        string accessibilityFilter, string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            AccessibilityFilter = accessibilityFilter,
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("App.Handlers", "H", new[] { "Handlers.cs:4", "Handlers.cs:9" })]
    [InlineData("App", "H", new[] { "Handlers.cs:4", "Handlers.cs:9", "Other.cs:5" })]
    [InlineData("App.Handlers", "Helper", new string[0])]
    public async Task FindReferences_NameFilterWithNamespaceFilter_AndsTheSiteFilters(
        string namespaceFilter, string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NamespaceFilter = namespaceFilter,
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_NameFilterWithReferenceFile_AndsTheScopes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            ReferenceFile = workspace.PathOf("Other.cs"),
            NameFilter = "H"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Other.cs:5" }, Sites(result.Data!.References));
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("Factory", new[] { "Consumer.cs:5" })]
    [InlineData("holder", new[] { "Consumer.cs:6" })]
    [InlineData("Widget", new string[0])]
    public async Task FindReferences_NameFilter_FieldTypeAndBaseListSitesUseTheirEnclosingSymbol(
        string nameFilter, string[] expected)
    {
        // A field initializer is enclosed by the field (a lambda in it looked through to the field); a
        // field declaration's type, with no initializer, by the containing type; and a top-level
        // type's base list lies outside the type's body, so it is a namespace-level site that never
        // matches a name filter (not even the derived type's own name).
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                namespace App;
                public class DerivedWidget : Target { }
                public class Holder
                {
                    private static readonly System.Func<object> TargetFactory = () => typeof(Target);
                    public Target? Plain;
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var unfiltered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false
        });
        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            NameFilter = nameFilter
        });

        Assert.True(unfiltered.Success);
        Assert.Equal(new[] { "Consumer.cs:2", "Consumer.cs:5", "Consumer.cs:6" }, Sites(unfiltered.Data!.References));
        Assert.True(result.Success, nameFilter);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_NameFilter_NamespaceLevelSitesNeverMatch()
    {
        // The InnerAlias using directive is enclosed by namespace App.Consumers, whose name contains
        // "Consumer"; namespace-level sites never match a name filter (same as accessibilityFilter).
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                using GlobalAlias = App.Target;
                namespace App.Consumers
                {
                    using InnerAlias = App.Target;
                    public class Consumer
                    {
                        public App.Target ConsumerMake() => null!;
                    }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            NameFilter = "Consumer"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Consumer.cs:7" }, Sites(result.Data!.References));
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_NameFilter_TopLevelStatementSitesNeverMatch()
    {
        // Top-level statements are enclosed by the synthesized <Main>$ entry point, which resolves to
        // the global namespace: a namespace-level site, so even "Main" does not match.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                public static class Target
                {
                    public static void Run() { }
                }
                """,
            ["Program.cs"] = """
                Target.Run();
                void Local() { Target.Run(); }
                """,
            ["Host.cs"] = """
                public class Host
                {
                    private void MainLoop() { Target.Run(); }
                }
                """
        }, "Target.cs");
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            NameFilter = "Main"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Host.cs:3" }, Sites(result.Data!.References));
    }

    #endregion

    #region KindFilter

    // Site-symbol KindFilter: Target.Run is referenced from a field initializer (_seed → Field), a
    // constructor (.ctor → Method), methods (Handle / Helper → Method), a block-bodied property getter
    // (get_Total → Method), and an expression-bodied property (get_Count → Method). Judged on
    // SymbolKindMapper.Map(DeclaringSiteSymbol) — the same site accessibilityFilter / nameFilter judge
    // (property accessors are Method, unlike find_callers CallingSymbol which reports the property).
    private static readonly Dictionary<string, string> KindSources = new()
    {
        ["Target.cs"] = """
            namespace App;
            public static class Target
            {
                public static int Run() => 0;
            }
            """,
        ["Sites.cs"] = """
            namespace App.Sites;
            public class Worker
            {
                private int _seed = App.Target.Run();
                public Worker() { App.Target.Run(); }
                public void Handle() { App.Target.Run(); }
                private int Helper() => App.Target.Run();
                public int Total { get { return App.Target.Run(); } }
                public int Count => App.Target.Run();
            }
            """
    };

    // Usages only (includeDeclaration false): six sites in Sites.cs. Line numbers are 1-based in the
    // file as written above (namespace=1, class=2, {=3, _seed=4, .ctor=5, Handle=6, Helper=7, Total=8, Count=9).
    private static readonly string[] AllKindUsageSites =
    [
        "Sites.cs:4", "Sites.cs:5", "Sites.cs:6", "Sites.cs:7", "Sites.cs:8", "Sites.cs:9"
    ];

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindReferences_BlankKindFilter_MatchesOmitted(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false
        });
        var filtered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = kindFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(AllKindUsageSites, Sites(omitted.Data!.References));
        Assert.Equal(AllKindUsageSites, Sites(filtered.Data!.References));
        Assert.Equal(AllKindUsageSites.Length, filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Method", new[] { "Sites.cs:5", "Sites.cs:6", "Sites.cs:7", "Sites.cs:8", "Sites.cs:9" })]
    [InlineData("method", new[] { "Sites.cs:5", "Sites.cs:6", "Sites.cs:7", "Sites.cs:8", "Sites.cs:9" })]
    [InlineData("  FIELD  ", new[] { "Sites.cs:4" })]
    [InlineData("Field", new[] { "Sites.cs:4" })]
    [InlineData("Property", new string[] { })]
    [InlineData("Constant", new string[] { })]
    [InlineData("Event", new string[] { })]
    [InlineData("Struct", new string[] { })]
    [InlineData("Class", new string[] { })]
    [InlineData("Local", new string[] { })]
    [InlineData("Namespace", new string[] { })]
    public async Task FindReferences_KindFilter_SelectsSitesByEnclosingSymbolKind(string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = kindFilter
        });

        Assert.True(result.Success, kindFilter);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Klass")]
    [InlineData("6")]
    [InlineData("Method, Property")]
    public async Task FindReferences_UnknownKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new FindReferencesParams
            {
                SourceFile = workspace.PathOf("Target.cs"),
                SymbolName = "Run",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task FindReferences_KindFilterWithMaxResults_CapsAfterKindFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = "Method",
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.References.Count);
        Assert.Equal(5, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(result.Data.References, r => Assert.Contains(
            $"{Path.GetFileName(r.File)}:{r.Line}",
            new[] { "Sites.cs:5", "Sites.cs:6", "Sites.cs:7", "Sites.cs:8", "Sites.cs:9" }));
    }

    [SkippableTheory]
    [InlineData("Method", "H", new[] { "Sites.cs:6", "Sites.cs:7" })]
    [InlineData("Method", "get_", new[] { "Sites.cs:8", "Sites.cs:9" })]
    [InlineData("Field", "seed", new[] { "Sites.cs:4" })]
    [InlineData("Field", "H", new string[0])]
    [InlineData("Property", "Total", new string[0])]
    public async Task FindReferences_KindFilterWithNameFilter_AndsTheSiteFilters(
        string kindFilter, string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = kindFilter,
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("private", "Method", new[] { "Sites.cs:7" })]
    [InlineData("public", "Method", new[] { "Sites.cs:5", "Sites.cs:6", "Sites.cs:8", "Sites.cs:9" })]
    [InlineData("private", "Field", new[] { "Sites.cs:4" })]
    [InlineData("public", "Field", new string[0])]
    public async Task FindReferences_KindFilterWithAccessibilityFilter_AndsTheSiteFilters(
        string accessibilityFilter, string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            AccessibilityFilter = accessibilityFilter,
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("App.Sites", "Field", new[] { "Sites.cs:4" })]
    [InlineData("App.Other", "Method", new string[0])]
    public async Task FindReferences_KindFilterWithNamespaceFilter_AndsTheSiteFilters(
        string namespaceFilter, string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            NamespaceFilter = namespaceFilter,
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_KindFilter_IncludeDeclarationJudgesDefinitionByDeclaredSymbol()
    {
        // Run's definition is a Method site; usages include Field + Method sites. Method filter keeps
        // the definition plus the five Method usages; Field keeps only the field initializer.
        await using var workspace = await TempWorkspace.CreateAsync(KindSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var method = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            KindFilter = "Method"
        });
        var field = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            KindFilter = "Field"
        });

        Assert.True(method.Success);
        Assert.Contains("Target.cs:4", Sites(method.Data!.References));
        Assert.Equal(6, method.Data.TotalCount); // definition + 5 Method usages
        Assert.DoesNotContain(method.Data.References, r => Path.GetFileName(r.File) == "Sites.cs" && r.Line == 4);

        Assert.True(field.Success);
        Assert.Equal(new[] { "Sites.cs:4" }, Sites(field.Data!.References));
        Assert.Equal(1, field.Data.TotalCount);
        Assert.All(field.Data.References, r => Assert.False(r.IsDefinition));
    }

    [SkippableFact]
    public async Task FindReferences_KindFilter_PropertyDefinitionIsProperty()
    {
        // A property declaration site maps to Property; usages of the property from a method are Method.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target
                {
                    public int Value { get; set; }
                }
                """,
            ["Consumer.cs"] = """
                namespace App;
                public class Consumer
                {
                    public int Read(Target t) => t.Value;
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var property = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Value",
            KindFilter = "Property"
        });
        var method = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Value",
            IncludeDeclaration = false,
            KindFilter = "Method"
        });

        Assert.True(property.Success);
        var definition = Assert.Single(property.Data!.References);
        Assert.True(definition.IsDefinition);
        Assert.True(SamePath(definition.File, workspace.PathOf("Target.cs")));

        Assert.True(method.Success);
        Assert.Equal(new[] { "Consumer.cs:4" }, Sites(method.Data!.References));
    }

    [SkippableFact]
    public async Task FindReferences_KindFilter_NamespaceLevelSitesNeverMatch()
    {
        // Using aliases and a top-level type's base list are namespace-level sites; even kindFilter
        // Namespace never matches them (same as accessibilityFilter / nameFilter). A typeof usage
        // inside a method body is a Method site; a method return-type annotation is a Class site
        // (containing type), so use a body usage for the Method control.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                using GlobalAlias = App.Target;
                namespace App.Consumers
                {
                    using InnerAlias = App.Target;
                    public class Derived : App.Target { }
                    public class Consumer
                    {
                        public void Use() { _ = typeof(App.Target); }
                    }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var unfiltered = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false
        });
        var method = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            KindFilter = "Method"
        });
        var ns = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            KindFilter = "Namespace"
        });

        Assert.True(unfiltered.Success);
        // using + using + base list + method-body typeof
        Assert.True(unfiltered.Data!.TotalCount >= 4);
        Assert.True(method.Success);
        Assert.Equal(new[] { "Consumer.cs:8" }, Sites(method.Data!.References));
        Assert.True(ns.Success);
        Assert.Empty(ns.Data!.References);
        Assert.Equal(0, ns.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_KindFilter_TopLevelStatementSitesNeverMatch()
    {
        // Top-level statements (including a top-level local function and lambda) are enclosed by the
        // synthesized <Main>$ entry point, which resolves to the global namespace: a namespace-level
        // site, so it never matches a kind filter, not even Method.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                public static class Target
                {
                    public static void Run() { }
                }
                """,
            ["Program.cs"] = """
                Target.Run();
                void Local() { Target.Run(); }
                System.Action a = () => Target.Run();
                """,
            ["Host.cs"] = """
                public class Host
                {
                    private void Work() { Target.Run(); }
                }
                """
        }, "Target.cs");
        var operation = new FindReferencesOperation(workspace.Context);

        var all = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false
        });
        var method = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = "Method"
        });

        Assert.True(all.Success);
        Assert.Equal(3, all.Data!.References.Count(r => Path.GetFileName(r.File) == "Program.cs"));
        Assert.True(method.Success);
        Assert.Equal(new[] { "Host.cs:3" }, Sites(method.Data!.References));
    }

    [SkippableFact]
    public async Task FindReferences_KindFilter_DelegateMethodSitesAreDelegate()
    {
        // A usage in a delegate declaration's signature is enclosed by the delegate's synthesized
        // Invoke / BeginInvoke / EndInvoke / .ctor, which are looked through to the delegate itself:
        // the site is Delegate, not Method. A usage in an ordinary method body is the Method control.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                namespace App;
                public delegate void Handler(App.Target t);
                public class Consumer
                {
                    public void Use() { _ = typeof(App.Target); }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var @delegate = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            KindFilter = "Delegate"
        });
        var method = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            KindFilter = "Method"
        });

        Assert.True(@delegate.Success);
        Assert.Equal(new[] { "Consumer.cs:2" }, Sites(@delegate.Data!.References));
        Assert.True(method.Success);
        Assert.Equal(new[] { "Consumer.cs:5" }, Sites(method.Data!.References));
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

    #region staticFilter (#3082)

    private static readonly Dictionary<string, string> StaticSources = new()
    {
        ["Target.cs"] = """
            namespace App;
            public static class Target
            {
                public static int Run() => 1;
            }
            """,
        ["Sites.cs"] = """
            namespace App.Sites;
            public class Sites
            {
                private static int s_seed = App.Target.Run();
                private int _seed = App.Target.Run();
                static Sites() { _ = App.Target.Run(); }
                public Sites() { _ = App.Target.Run(); }
                public static int S() { System.Func<int> f = () => App.Target.Run(); return f(); }
                public int I() { return Local(); static int Local() => App.Target.Run(); }
                public static int P => App.Target.Run();
                public int Q => App.Target.Run();
                public static Sites operator +(Sites a, Sites b) { _ = App.Target.Run(); return a; }
            }
            public static class Helpers
            {
                public static void H() { _ = App.Target.Run(); }
            }
            """
    };

    private static readonly string[] StaticUsageSites =
        { "Sites.cs:4", "Sites.cs:6", "Sites.cs:8", "Sites.cs:10", "Sites.cs:12", "Sites.cs:16" };

    private static readonly string[] InstanceUsageSites =
        { "Sites.cs:5", "Sites.cs:7", "Sites.cs:9", "Sites.cs:11" };

    [Fact]
    public void FindReferencesParams_StaticFilter_DefaultsToNull()
    {
        var @params = new FindReferencesParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.StaticFilter);
    }

    [SkippableFact]
    public async Task FindReferences_StaticFilterOmitted_ReportsEverySite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false
        });

        Assert.True(result.Success);
        var expected = StaticUsageSites.Concat(InstanceUsageSites)
            .OrderBy(site => int.Parse(site.Split(':')[1]))
            .ToArray();
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindReferences_StaticFilter_SelectsSitesByEnclosingSymbolStaticness(bool staticFilter)
    {
        // Static field initializer, static constructor, a lambda in a static method (judged on the
        // method), static property, operator, and a member of a static class are static sites; the
        // instance field initializer, instance constructor, a static local function inside an instance
        // method (judged on the declaring instance method), and an instance property are instance sites.
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            StaticFilter = staticFilter
        });

        var expected = staticFilter ? StaticUsageSites : InstanceUsageSites;
        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task FindReferences_StaticFilterWithMaxResults_CapsAfterStaticFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            StaticFilter = false,
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.References.Count);
        Assert.Equal(InstanceUsageSites.Length, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
        Assert.All(Sites(result.Data.References), site => Assert.Contains(site, InstanceUsageSites));
    }

    [SkippableTheory]
    [InlineData("Method", true, new[] { "Sites.cs:6", "Sites.cs:8", "Sites.cs:10", "Sites.cs:12", "Sites.cs:16" })]
    [InlineData("Field", true, new[] { "Sites.cs:4" })]
    [InlineData("Field", false, new[] { "Sites.cs:5" })]
    [InlineData("Event", true, new string[0])]
    public async Task FindReferences_StaticFilterWithKindFilter_AndsTheSiteFilters(
        string kindFilter, bool staticFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            IncludeDeclaration = false,
            KindFilter = kindFilter,
            StaticFilter = staticFilter
        });

        Assert.True(result.Success);
        Assert.Equal(expected, Sites(result.Data!.References));
        Assert.Equal(expected.Length, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task FindReferences_StaticFilter_IncludeDeclarationJudgesDefinitionByDeclaredSymbol()
    {
        // Run is static, so its definition is kept by true and dropped by false.
        await using var workspace = await TempWorkspace.CreateAsync(StaticSources);
        var operation = new FindReferencesOperation(workspace.Context);

        var statics = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            StaticFilter = true
        });
        var instances = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Run",
            StaticFilter = false
        });

        Assert.True(statics.Success);
        Assert.Contains(statics.Data!.References, r => r.IsDefinition);
        Assert.Equal(StaticUsageSites.Length + 1, statics.Data.TotalCount);

        Assert.True(instances.Success);
        Assert.DoesNotContain(instances.Data!.References, r => r.IsDefinition);
        Assert.Equal(InstanceUsageSites, Sites(instances.Data.References));
    }

    [SkippableFact]
    public async Task FindReferences_StaticFilter_NamespaceLevelSitesNeverMatch_DelegateSiteIsInstance()
    {
        // Using aliases and a type's base list are namespace-level sites and never match either value.
        // A delegate parameter site is judged on the delegate type, which is never static.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Target.cs"] = """
                namespace App;
                public class Target { }
                """,
            ["Consumer.cs"] = """
                using GlobalAlias = App.Target;
                namespace App.Consumers
                {
                    public class Derived : App.Target { }
                    public delegate void Del(App.Target t);
                    public static class Consumer
                    {
                        public static void Use() { _ = typeof(App.Target); }
                    }
                }
                """
        });
        var operation = new FindReferencesOperation(workspace.Context);

        var statics = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            StaticFilter = true
        });
        var instances = await operation.ExecuteAsync(new FindReferencesParams
        {
            SourceFile = workspace.PathOf("Target.cs"),
            SymbolName = "Target",
            IncludeDeclaration = false,
            StaticFilter = false
        });

        Assert.True(statics.Success);
        Assert.Equal(new[] { "Consumer.cs:8" }, Sites(statics.Data!.References));
        Assert.True(instances.Success);
        Assert.Equal(new[] { "Consumer.cs:5" }, Sites(instances.Data!.References));
        Assert.Equal(1, instances.Data.TotalCount);
    }

    #endregion

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

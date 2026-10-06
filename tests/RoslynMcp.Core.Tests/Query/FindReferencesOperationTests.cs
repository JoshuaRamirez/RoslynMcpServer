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
/// (restricts reported locations to one file; peer of <c>find_callers</c> <c>callerFile</c>).
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

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
}

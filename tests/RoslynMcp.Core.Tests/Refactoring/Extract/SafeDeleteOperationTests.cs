using System.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.FileSystem;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Refactoring.Utilities;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="SafeDeleteOperation"/>.
/// </summary>
public class SafeDeleteOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = "",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = "Types.cs",
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 0,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidSelectionRange_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 2,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.InvalidSelectionRange, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                SourceFile = AbsoluteTestPath(),
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                AllFiles = false,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrSelection_DoesNotThrow()
    {
        SafeDeleteOperation.Validate(new SafeDeleteParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithStartLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                AllFiles = true,
                StartLine = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithSymbolName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            SafeDeleteOperation.Validate(new SafeDeleteParams
            {
                AllFiles = true,
                SymbolName = "_unused"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Safe-delete unused symbol", SafeDeleteOperation.BuildAllFilesDescription(1));
        Assert.Equal("Safe-delete 2 unused symbols", SafeDeleteOperation.BuildAllFilesDescription(2));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task SafeDelete_UnusedField_RemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _unused;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_unused");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "_unused"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("_unused", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("_unused", updated);
        Assert.Contains("return 42;", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_UnusedMethod_RemovesDeclaration()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }

                private void UnusedHelper()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "UnusedHelper");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("UnusedHelper", updated);
        Assert.Contains("public int Get()", updated);
    }

    #endregion

    #region P0 Preview

    [SkippableFact]
    public async Task SafeDelete_Preview_DoesNotModifyFile()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _unused;

                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_unused");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains(result.PendingChanges, change => change.Description.Contains("_unused"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region P0 Rejects

    [SkippableFact]
    public async Task SafeDelete_InUseField_ThrowsWithLocations()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                private int _used;

                public int Get()
                {
                    return _used;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "_used");
        var usage = FindSpan(source, "return _used;");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "_used"
            }));

        Assert.Equal(ErrorCodes.MemberHasUsages, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.True(ex.Details.ContainsKey("locations"));
        Assert.Equal(1, Convert.ToInt32(ex.Details["usageCount"]));

        var locations = Assert.IsAssignableFrom<IEnumerable>(ex.Details["locations"]);
        var locationMaps = locations.Cast<Dictionary<string, object>>().ToList();
        Assert.NotEmpty(locationMaps);
        Assert.Contains(locationMaps, location =>
            Convert.ToInt32(location["line"]) == usage.StartLine &&
            location["snippet"] is string snippet &&
            snippet.Contains("_used"));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task SafeDelete_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "return");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SafeDelete_BodyText_ThrowsSymbolNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "42");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Contains("public int Get()", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_ForeachVariable_ThrowsWithoutDeletingMethod()
    {
        const string source = """
            namespace TestApp;

            public class Calculator
            {
                public void Walk(int[] items)
                {
                    foreach (var unusedItem in items)
                    {
                    }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "unusedItem");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn,
                EndLine = span.EndLine,
                EndColumn = span.EndColumn,
                SymbolName = "unusedItem"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void Walk(int[] items)", before);
    }

    [SkippableFact]
    public async Task SafeDelete_UnusedInterfaceImplementation_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ICalculator
            {
                int Get();
            }

            public class Calculator : ICalculator
            {
                public int Get()
                {
                    return 42;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(source, "public int Get()");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                SourceFile = workspace.SourcePath,
                StartLine = span.StartLine,
                StartColumn = span.StartColumn + "public int ".Length,
                EndLine = span.EndLine,
                EndColumn = span.StartColumn + "public int Get".Length,
                SymbolName = "Get"
            }));

        Assert.Equal(ErrorCodes.MemberHasUsages, ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.True(Convert.ToInt32(ex.Details["usageCount"]) >= 1);
        var locations = Assert.IsAssignableFrom<IEnumerable>(ex.Details["locations"]);
        var locationMaps = locations.Cast<Dictionary<string, object>>().ToList();
        Assert.Contains(locationMaps, location =>
            location["snippet"] is string snippet &&
            snippet.Contains("implements", StringComparison.Ordinal) &&
            snippet.Contains("Get", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Get()", before);
    }

    [Fact]
    public void SafeDelete_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            DocumentEditableHelpers.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region Helpers


    #region AllFiles

    private const string UnusedMembersFileA = """
        namespace TestApp;

        public class FileA
        {
            private int _unusedA;

            public int Get()
            {
                return 42;
            }

            private void UnusedHelperA()
            {
            }
        }
        """;

    private const string UnusedMembersFileB = """
        namespace TestApp;

        public class FileB
        {
            private string _unusedB;

            public string Name()
            {
                return "ok";
            }
        }
        """;

    private const string UsedMembersFileC = """
        namespace TestApp;

        public class FileC
        {
            private int _used;

            public int Get()
            {
                return _used;
            }
        }
        """;

    [SkippableFact]
    public async Task SafeDelete_AllFilesFalse_DeletesOnlySpecifiedSymbol()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA),
            ("FileB.cs", UnusedMembersFileB));
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(UnusedMembersFileA, "_unusedA");
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            SymbolName = "_unusedA"
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("_unusedA", updatedA);
        Assert.Contains("UnusedHelperA", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task SafeDelete_OmittedAllFiles_KeepsSingleSiteDelete()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnusedMembersFileA);
        var operation = new SafeDeleteOperation(workspace.Context);
        var span = FindSpan(UnusedMembersFileA, "UnusedHelperA");

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            SourceFile = workspace.SourcePath,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("UnusedHelperA", updated);
        Assert.Contains("_unusedA", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_DeletesUnusedMembersAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA),
            ("FileB.cs", UnusedMembersFileB),
            ("FileC.cs", UsedMembersFileC));
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        var updatedC = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.DoesNotContain("_unusedA", updatedA);
        Assert.DoesNotContain("UnusedHelperA", updatedA);
        Assert.Contains("return 42;", updatedA);
        Assert.DoesNotContain("_unusedB", updatedB);
        Assert.Contains("return \"ok\";", updatedB);
        Assert.Contains("_used", updatedC);
        Assert.Contains("return _used;", updatedC);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_WithoutSourceFileOrSelection_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA));
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnusedMembersFileA);
        var operation = new SafeDeleteOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = false,
                StartLine = 1,
                StartColumn = 1,
                EndLine = 1,
                EndColumn = 2
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_WithStartLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnusedMembersFileA);
        var operation = new SafeDeleteOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                StartLine = 4
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_WithSymbolName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(UnusedMembersFileA);
        var operation = new SafeDeleteOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                SymbolName = "_unusedA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task SafeDelete_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA),
            ("FileB.cs", UnusedMembersFileB));
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges.Count >= 2);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", UsedMembersFileC));
        var before = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes?.FilesModified ?? []);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsWhenUsagesExist()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", UsedMembersFileC),
            ("FileA.cs", UnusedMembersFileA));
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedC = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Contains("_used", updatedC);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("_unusedA", updatedA);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA),
            ("FileB.cs", UnusedMembersFileB));
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain("_unusedA", updatedA);
        Assert.DoesNotContain("UnusedHelperA", updatedA);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA),
            ("FileB.cs", UnusedMembersFileB));
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var operation = new SafeDeleteOperation(workspace.Context);
        var aliased = workspace.SourcePaths["FileA.cs"].Replace("FileA.cs", "filea.cs", StringComparison.Ordinal);

        if (File.Exists(aliased))
        {
            var result = await operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                SourceFile = aliased
            });

            Assert.True(result.Success);
            var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
            Assert.DoesNotContain("_unusedA", updatedA);
            Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
            return;
        }

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                SourceFile = aliased
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_OptionalSourceFile_AmbiguousIgnoreCase_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            [("FileA.cs", UnusedMembersFileA), ("filea.cs", UnusedMembersFileB)],
            explicitCompileItems: true);
        Skip.If(
            string.Equals(
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["FileA.cs"]),
                PathResolver.GetPathComparisonKey(workspace.SourcePaths["filea.cs"]),
                StringComparison.Ordinal),
            "Volume does not preserve case-distinct paths.");
        var operation = new SafeDeleteOperation(workspace.Context);
        var ambiguous = FlipPathFileNameAsciiCase(workspace.SourcePaths["FileA.cs"]);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                SourceFile = ambiguous
            }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Contains("exact file path casing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }


    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsStaticConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public class Host
            {
                static Host()
                {
                    System.Console.WriteLine("init");
                }

                private static void Unused()
                {
                }

                public static int Run() => 1;
            }

            public static class Driver
            {
                public static int Go() => Host.Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("static Host()", updated);
        Assert.DoesNotContain("Unused()", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsUsingVarLocal()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public class Host
            {
                public int Run()
                {
                    using var scope = (System.IDisposable)null!;
                    return 1;
                }

                private int _unusedField;
            }

            public static class Driver
            {
                public static int Go() => new Host().Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("using var scope", updated);
        Assert.DoesNotContain("_unusedField", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsPrivateMainEntryPoint()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public static class Program
            {
                private static void Main()
                {
                }

                private static void UnusedHelper()
                {
                }
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Main()", updated);
        Assert.DoesNotContain("UnusedHelper", updated);
    }


    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsEffectfulFieldAndLocalInitializers()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public class Host
            {
                private static readonly object Registration = Register();
                private static int _unusedPure = 0;

                private static object Register() => new object();
                private static int Mutate() => 1;

                public static int Run()
                {
                    var removed = Mutate();
                    return 1;
                }
            }

            public static class Driver
            {
                public static int Go() => Host.Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Registration = Register()", updated);
        Assert.Contains("var removed = Mutate()", updated);
        Assert.DoesNotContain("_unusedPure", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsEffectfulPropertyInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public class Host
            {
                private object Registration { get; } = Register();
                private int UnusedProp { get; }

                private static object Register() => new object();

                public static int Run() => 1;
            }

            public static class Driver
            {
                public static int Go() => Host.Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Registration { get; } = Register()", updated);
        Assert.DoesNotContain("UnusedProp", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsPrivateConstructorThatSuppressesPublicConstruction()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public class Utility
            {
                private Utility()
                {
                }

                private static void UnusedHelper()
                {
                }

                public static int Run() => 1;
            }

            public static class Driver
            {
                public static int Go() => Utility.Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private Utility()", updated);
        Assert.DoesNotContain("UnusedHelper", updated);
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_SkipsEnumMemberThatWouldRenumberLaterValues()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace TestApp;

            public enum Mode
            {
                Legacy,
                Current
            }

            public class Host
            {
                private static void UnusedHelper()
                {
                }

                public static int Run() => (int)Mode.Current;
            }

            public static class Driver
            {
                public static int Go() => Host.Run();
            }
            """);
        var operation = new SafeDeleteOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SafeDeleteParams { AllFiles = true });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("Legacy,", updated);
        Assert.Contains("Current", updated);
        Assert.DoesNotContain("UnusedHelper", updated);
    }

    [Fact]
    public void IsPureInitializer_AllowsLiteralsDefaultTypeofNameof_RejectsCalls()
    {
        var literal = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("42");
        var call = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("Register()");
        var nameofExpr = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("nameof(Host)");
        var typeofExpr = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("typeof(int)");
        var defaultExpr = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseExpression("default(int)");

        Assert.True(SafeDeleteOperation.IsPureInitializer(literal));
        Assert.True(SafeDeleteOperation.IsPureInitializer(nameofExpr));
        Assert.True(SafeDeleteOperation.IsPureInitializer(typeofExpr));
        Assert.True(SafeDeleteOperation.IsPureInitializer(defaultExpr));
        Assert.False(SafeDeleteOperation.IsPureInitializer(call));
    }

    [SkippableFact]
    public async Task SafeDelete_AllFilesTrue_OptionalSourceFile_MissingOnDisk_Throws()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", UnusedMembersFileA));
        var operation = new SafeDeleteOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Missing.cs");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SafeDeleteParams
            {
                AllFiles = true,
                SourceFile = missing
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    #endregion

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpSafeDeleteMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FlipPathFileNameAsciiCase(string path)
    {
        var fileName = Path.GetFileName(path);
        var flipped = new string(fileName.Select(static c =>
            c is >= 'a' and <= 'z' ? char.ToUpperInvariant(c) :
            c is >= 'A' and <= 'Z' ? char.ToLowerInvariant(c) :
            c).ToArray());
        return Path.Combine(Path.GetDirectoryName(path)!, flipped);
    }

    private static (int StartLine, int StartColumn, int EndLine, int EndColumn) FindSpan(string source, string snippet)
    {
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        return (GetLineColumn(source, index).Line, GetLineColumn(source, index).Column,
            GetLineColumn(source, index + snippet.Length).Line, GetLineColumn(source, index + snippet.Length).Column);
    }

    private static (int Line, int Column) GetLineColumn(string source, int index)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateWithFilesAsync(files, explicitCompileItems: false);

        public static async Task<TempWorkspace> CreateWithFilesAsync(
            IReadOnlyList<(string FileName, string Source)> files,
            bool explicitCompileItems)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSafeDelete_" + Guid.NewGuid().ToString("N"));
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
                    ProjectPath = projectPath,
                    SourcePath = sourcePaths[files[0].FileName],
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

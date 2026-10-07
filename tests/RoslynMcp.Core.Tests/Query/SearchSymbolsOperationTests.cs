using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="SearchSymbolsOperation"/> optional <c>sourceFile</c>
/// filtering (peer of <see cref="GetDiagnosticsOperation"/>), optional <c>caseSensitive</c>
/// name matching, optional <c>exactMatch</c> whole-name matching, optional
/// <c>namespaceFilter</c> namespace scoping, and optional <c>projectPath</c> project scoping.
/// </summary>
public class SearchSymbolsOperationTests
{
    private static string AbsoluteTestPath(string extension = ".cs") =>
        OperatingSystem.IsWindows()
            ? $"C:\\test\\file{extension}"
            : $"/test/file{extension}";

    #region Input Validation

    [Fact]
    public void SourceFile_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.SourceFile);
    }

    [SkippableFact]
    public async Task Validate_RelativeSourceFile_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = "file.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Validate_NonCsSourceFile_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = AbsoluteTestPath(".txt")
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task Validate_MissingSourceFile_ThrowsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableTheory]
    [InlineData("6")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("Class, Method")]
    public async Task Execute_NumericUndefinedOrCombinedKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
    }

    #endregion

    #region Execute filtering

    [SkippableFact]
    public async Task SearchSymbols_OmittedSourceFile_ReturnsMatchesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Unique"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueBeta");
        Assert.True(result.Data.TotalCount >= 2);
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_LimitsToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Unique",
            SourceFile = alphaPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
        Assert.DoesNotContain(result.Data.Symbols, s => s.Name == "UniqueBeta");
        Assert.All(result.Data.Symbols, s =>
            Assert.True(
                string.Equals(s.File, alphaPath, StringComparison.OrdinalIgnoreCase),
                $"Expected file {alphaPath}, got {s.File}"));
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_MatchesPartialDeclaredInRequestedFile()
    {
        // First location is PartA; request PartB so FirstOrDefault-only would wrongly miss.
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("PartA.cs", "partial class UniquePartial { public void A() { } }\n"),
            ("PartB.cs", "partial class UniquePartial { public void B() { } }\n"));

        var partB = workspace.SourcePaths["PartB.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "UniquePartial",
            SourceFile = partB
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniquePartial");
        Assert.All(result.Data.Symbols.Where(s => s.Name == "UniquePartial"), s =>
            Assert.True(
                string.Equals(s.File, partB, StringComparison.OrdinalIgnoreCase),
                $"Expected file {partB}, got {s.File}"));
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_CaseInsensitivePathMatch()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class UniqueAlpha {}\n"),
            ("Beta.cs", "class UniqueBeta {}\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var flipped = FlipAsciiCase(Path.GetFileName(alphaPath));
        var queryPath = Path.Combine(Path.GetDirectoryName(alphaPath)!, flipped);
        // File.Exists must succeed; on case-sensitive FS the flipped path may not exist.
        // Only run the case-flip path when the OS resolves it to the same file.
        if (!File.Exists(queryPath))
        {
            queryPath = alphaPath;
        }

        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "UniqueAlpha",
            SourceFile = queryPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data.Symbols, s => s.Name == "UniqueAlpha");
    }

    [SkippableFact]
    public async Task SearchSymbols_WithSourceFile_ExistingOutsideWorkspace_ReturnsSuccessEmpty()
    {
        // Existing absolute .cs outside the loaded workspace is valid: File.Exists
        // passes and path filtering yields no declarations (not SourceNotInWorkspace).
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var outsideDir = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbols_Outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsidePath = Path.Combine(outsideDir, "Outside.cs");

        try
        {
            await File.WriteAllTextAsync(outsidePath, "class OutsideUnique { }\n");

            var operation = new SearchSymbolsOperation(workspace.Context);
            var result = await operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                SourceFile = outsidePath
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data.Symbols);
            Assert.Equal(0, result.Data.TotalCount);
        }
        finally
        {
            try
            {
                Directory.Delete(outsideDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    #endregion

    #region caseSensitive

    // Mixed-case names: case-insensitive "ID" matches all of Ids/Id/ID/Identity/Validate/Width/IDGenerator;
    // case-sensitive "ID" matches only ID and IDGenerator.
    private const string MixedCaseSource = """
        class Ids
        {
            public int Id;
            public int ID;
            public int Identity;
            public void Validate() { }
            public int Width;
        }
        class IDGenerator { }
        """;

    private static readonly string[] CaseInsensitiveIdNames =
        ["Ids", "Id", "ID", "Identity", "Validate", "Width", "IDGenerator"];

    [Fact]
    public void CaseSensitive_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.CaseSensitive);
    }

    [SkippableFact]
    public async Task SearchSymbols_OmittedCaseSensitive_MatchesCaseInsensitively()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID" });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var names = result.Data.Symbols.Select(s => s.Name).ToList();
        Assert.All(CaseInsensitiveIdNames, n => Assert.Contains(n, names));
        Assert.Equal(CaseInsensitiveIdNames.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveFalse_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID" });
        var explicitFalse = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID", CaseSensitive = false });

        Assert.True(omitted.Success);
        Assert.True(explicitFalse.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(explicitFalse.Data);
        Assert.Equal(omitted.Data.TotalCount, explicitFalse.Data.TotalCount);
        Assert.Equal(omitted.Data.Truncated, explicitFalse.Data.Truncated);
        Assert.Equal(
            omitted.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)),
            explicitFalse.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)));
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_MatchesExactCaseOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "ID", CaseSensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[] { "ID", "IDGenerator" },
            result.Data.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_LowercaseQuery_MatchesLowercaseSubstringOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "id", CaseSensitive = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[] { "Validate", "Width" },
            result.Data.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithKindFilter_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var fields = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            KindFilter = "Field"
        });
        var classes = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            KindFilter = "Class"
        });

        Assert.True(fields.Success);
        Assert.NotNull(fields.Data);
        var field = Assert.Single(fields.Data.Symbols);
        Assert.Equal("ID", field.Name);
        Assert.Equal(1, fields.Data.TotalCount);

        Assert.True(classes.Success);
        Assert.NotNull(classes.Data);
        var type = Assert.Single(classes.Data.Symbols);
        Assert.Equal("IDGenerator", type.Name);
        Assert.Equal(1, classes.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithMaxResults_TotalCountReflectsCaseSensitiveSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedCaseSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var sensitive = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            MaxResults = 1
        });
        var insensitive = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            MaxResults = 1
        });

        Assert.True(sensitive.Success);
        Assert.NotNull(sensitive.Data);
        var only = Assert.Single(sensitive.Data.Symbols);
        Assert.Contains(only.Name, new[] { "ID", "IDGenerator" });
        Assert.Equal(2, sensitive.Data.TotalCount);
        Assert.True(sensitive.Data.Truncated);

        Assert.True(insensitive.Success);
        Assert.NotNull(insensitive.Data);
        Assert.Single(insensitive.Data.Symbols);
        Assert.Equal(CaseInsensitiveIdNames.Length, insensitive.Data.TotalCount);
        Assert.True(insensitive.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_CaseSensitiveTrue_WithSourceFile_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class IDAlpha { public int Id; }\n"),
            ("Beta.cs", "class IDBeta { public int ID; }\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "ID",
            CaseSensitive = true,
            SourceFile = alphaPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Symbols);
        Assert.Equal("IDAlpha", only.Name);
        Assert.True(
            string.Equals(only.File, alphaPath, StringComparison.OrdinalIgnoreCase),
            $"Expected file {alphaPath}, got {only.File}");
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region exactMatch

    // Substring "Add" matches Add/AddRange/TryAdd/Padding/add/Adder; exact (case-insensitive)
    // matches Add and add; exact + caseSensitive matches only Add.
    private const string ExactMatchSource = """
        class Adder
        {
            public void Add() { }
            public void AddRange() { }
            public bool TryAdd() => true;
            public int Padding;
            public int add;
        }
        class Add { }
        """;

    private static readonly string[] SubstringAddNames =
        ["Adder", "Add", "AddRange", "TryAdd", "Padding", "add"];

    [Fact]
    public void ExactMatch_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.ExactMatch);
    }

    [SkippableFact]
    public async Task SearchSymbols_OmittedExactMatch_MatchesSubstrings()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add" });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var names = result.Data.Symbols.Select(s => s.Name).ToList();
        Assert.All(SubstringAddNames, n => Assert.Contains(n, names));
        // Method Add and class Add are distinct symbols.
        Assert.Equal(SubstringAddNames.Length + 1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchFalse_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add" });
        var explicitFalse = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = false });

        Assert.True(omitted.Success);
        Assert.True(explicitFalse.Success);
        Assert.NotNull(omitted.Data);
        Assert.NotNull(explicitFalse.Data);
        Assert.Equal(omitted.Data.TotalCount, explicitFalse.Data.TotalCount);
        Assert.Equal(omitted.Data.Truncated, explicitFalse.Data.Truncated);
        Assert.Equal(
            omitted.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)),
            explicitFalse.Data.Symbols.Select(s => (s.FullyQualifiedName, s.Kind, s.Line, s.Column)));
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_MatchesWholeNameCaseInsensitively()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "add", ExactMatch = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[] { "Add", "Add", "add" },
            result.Data.Symbols.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_CaseSensitiveTrue_MatchesExactNameAndCaseOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var upper = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = true, CaseSensitive = true });
        var lower = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "add", ExactMatch = true, CaseSensitive = true });

        Assert.True(upper.Success);
        Assert.NotNull(upper.Data);
        Assert.All(upper.Data.Symbols, s => Assert.Equal("Add", s.Name));
        Assert.Equal(
            new[] { "Add", "Adder.Add()" },
            upper.Data.Symbols.Select(s => s.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, upper.Data.TotalCount);
        Assert.False(upper.Data.Truncated);

        Assert.True(lower.Success);
        Assert.NotNull(lower.Data);
        var field = Assert.Single(lower.Data.Symbols);
        Assert.Equal("add", field.Name);
        Assert.Equal(1, lower.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_WithKindFilter_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var methods = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = true, KindFilter = "Method" });
        var classes = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = true, KindFilter = "Class" });

        Assert.True(methods.Success);
        Assert.NotNull(methods.Data);
        var method = Assert.Single(methods.Data.Symbols);
        Assert.Equal("Add", method.Name);
        Assert.Equal(1, methods.Data.TotalCount);

        Assert.True(classes.Success);
        Assert.NotNull(classes.Data);
        var type = Assert.Single(classes.Data.Symbols);
        Assert.Equal("Add", type.Name);
        Assert.Equal("Add", type.FullyQualifiedName);
        Assert.Equal(1, classes.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_WithMaxResults_TotalCountReflectsExactSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var exact = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = true, MaxResults = 1 });
        var exactAboveCount = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Add", ExactMatch = true, MaxResults = 3 });

        Assert.True(exact.Success);
        Assert.NotNull(exact.Data);
        var only = Assert.Single(exact.Data.Symbols);
        Assert.Equal("add", only.Name, ignoreCase: true);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.True(exact.Data.Truncated);

        Assert.True(exactAboveCount.Success);
        Assert.NotNull(exactAboveCount.Data);
        Assert.Equal(3, exactAboveCount.Data.Symbols.Count);
        Assert.Equal(3, exactAboveCount.Data.TotalCount);
        Assert.False(exactAboveCount.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_WithSourceFile_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateMultiFileAsync(
            ("Alpha.cs", "class Alpha { public void Run() { } public void RunAll() { } }\n"),
            ("Beta.cs", "class Beta { public void Run() { } }\n"));

        var alphaPath = workspace.SourcePaths["Alpha.cs"];
        var operation = new SearchSymbolsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Run",
            ExactMatch = true,
            SourceFile = alphaPath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Symbols);
        Assert.Equal("Alpha.Run()", only.FullyQualifiedName);
        Assert.True(
            string.Equals(only.File, alphaPath, StringComparison.OrdinalIgnoreCase),
            $"Expected file {alphaPath}, got {only.File}");
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_ExactMatchTrue_NoWholeNameMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExactMatchSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        // "Ad" is a substring of several names but the whole name of none.
        var result = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Ad", ExactMatch = true });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Symbols);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region namespaceFilter

    // "Widget" is declared in App.Services, App.Services.Orders (also as a nested-type member),
    // App.ServicesExtra, App, app.services (different case) and the global namespace.
    private const string NamespaceFilterSource = """
        namespace App.Services
        {
            class Widget { }
            class Factory { public void Widget() { } }
        }
        namespace App.Services.Orders
        {
            class Widget { }
            class Outer { class Inner { public int Widget; } }
        }
        namespace App.ServicesExtra
        {
            class Widget { }
        }
        namespace App
        {
            class Widget { }
        }
        namespace app.services
        {
            class Widget { }
        }
        class Widget { }
        """;

    private static readonly string[] AllWidgetNames =
    [
        "App.Services.Widget",
        "App.Services.Factory.Widget()",
        "App.Services.Orders.Widget",
        "App.Services.Orders.Outer.Inner.Widget",
        "App.ServicesExtra.Widget",
        "App.Widget",
        "app.services.Widget",
        "Widget"
    ];

    private static readonly string[] AppServicesWidgetNames =
    [
        "App.Services.Factory.Widget()",
        "App.Services.Orders.Outer.Inner.Widget",
        "App.Services.Orders.Widget",
        "App.Services.Widget"
    ];

    private static List<string> SortedFqns(SearchSymbolsResult data) =>
        data.Symbols.Select(s => s.FullyQualifiedName).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchSymbols_OmittedOrBlankNamespaceFilter_SearchesAllNamespaces(string? namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            CaseSensitive = true,
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(AllWidgetNames.OrderBy(n => n, StringComparer.Ordinal), SortedFqns(result.Data));
        Assert.Equal(AllWidgetNames.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_IncludesExactAndNestedNamespacesOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        // Excludes App.ServicesExtra (sibling prefix), App (parent), app.services (case) and global.
        Assert.Equal(AppServicesWidgetNames, SortedFqns(result.Data));
        Assert.Equal(AppServicesWidgetNames.Length, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_NestedTypeMemberUsesEnclosingNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            KindFilter = "Field",
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var field = Assert.Single(result.Data.Symbols);
        Assert.Equal("App.Services.Orders.Outer.Inner.Widget", field.FullyQualifiedName);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_IsCaseSensitive()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = "app.services"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Symbols);
        Assert.Equal("app.services.Widget", only.FullyQualifiedName);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("global::App.Services")]
    [InlineData("  App.Services  ")]
    [InlineData(" global::App.Services ")]
    public async Task SearchSymbols_NamespaceFilter_TrimsAndAcceptsGlobalPrefix(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(AppServicesWidgetNames, SortedFqns(result.Data));
        Assert.Equal(AppServicesWidgetNames.Length, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_ParentNamespaceIncludesAllDescendants()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var expected = AllWidgetNames
            .Where(n => n.StartsWith("App.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, SortedFqns(result.Data));
        Assert.DoesNotContain("Widget", SortedFqns(result.Data));
        Assert.Equal(expected.Count, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_WithKindFilter_AppliesBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            KindFilter = "Method",
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var method = Assert.Single(result.Data.Symbols);
        Assert.Equal("App.Services.Factory.Widget()", method.FullyQualifiedName);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilter_WithMaxResults_TotalCountReflectsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = "App.Services",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        var only = Assert.Single(capped.Data.Symbols);
        Assert.Contains(only.FullyQualifiedName, AppServicesWidgetNames);
        Assert.Equal(AppServicesWidgetNames.Length, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Missing")]
    [InlineData("App.Serv")]
    [InlineData("Services")]
    public async Task SearchSymbols_NamespaceFilter_NoMatch_ReturnsEmptySuccess(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        // Partial segments ("App.Serv") and inner segments ("Services") are not namespace prefixes.
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Symbols);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task SearchSymbols_NamespaceFilterBareGlobalPrefix_SearchesAllNamespaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        // "global::" names the root namespace, which every namespace is nested in.
        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ExactMatch = true,
            CaseSensitive = true,
            NamespaceFilter = "global::"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(AllWidgetNames.OrderBy(n => n, StringComparer.Ordinal), SortedFqns(result.Data));
        Assert.Equal(AllWidgetNames.Length, result.Data.TotalCount);
    }

    #endregion

    #region projectPath

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new SearchSymbolsParams { Query = "Foo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
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
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams { Query = "UniqueAlpha", ProjectPath = projectPath }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams
            {
                Query = "UniqueAlpha",
                ProjectPath = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TempWorkspace.CreateAsync("class UniqueAlpha {}");
        var operation = new SearchSymbolsOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new SearchSymbolsParams { Query = "UniqueAlpha", ProjectPath = missing }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathOmittedOrBlank_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "SharedName" });
        var blank = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "SharedName", ProjectPath = "  " });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Contains(omitted.Data!.Symbols, s => s.FullyQualifiedName == "Lib.SharedName");
        Assert.Contains(omitted.Data.Symbols, s => s.FullyQualifiedName == "App.SharedName");
        Assert.Equal(2, omitted.Data.TotalCount);
        Assert.Equal(
            omitted.Data.Symbols.Select(s => s.FullyQualifiedName),
            blank.Data!.Symbols.Select(s => s.FullyQualifiedName));
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);

        var lib = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "SharedName",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        var libSymbol = Assert.Single(lib.Data!.Symbols);
        Assert.Equal("Lib.SharedName", libSymbol.FullyQualifiedName);
        Assert.Equal(workspace.LibSourcePath, libSymbol.File);
        Assert.Equal(1, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);

        var app = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "SharedName",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        var appSymbol = Assert.Single(app.Data!.Symbols);
        Assert.Equal("App.SharedName", appSymbol.FullyQualifiedName);
        Assert.Equal(workspace.AppSourcePath, appSymbol.File);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPath_ExcludesReferencedProjectDeclarations()
    {
        // App references Lib, but App's compilation only yields symbols declared in App's sources.
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Animal",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Symbols);
        Assert.Equal(0, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.LibProjectPath)!, "LIB.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new SearchSymbolsParams { Query = "SharedName", ProjectPath = wrongCased };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            var symbol = Assert.Single(result.Data!.Symbols);
            Assert.Equal("Lib.SharedName", symbol.FullyQualifiedName);
        }
        else
        {
            // Case-sensitive volume (Linux): LIB.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathWithMaxResults_CapsAfterProjectFilter()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            libSource: "namespace Lib { public class Widget1 {} public class Widget2 {} public class Widget3 {} }\n",
            appSource: "namespace App { public class Widget4 {} public class Widget5 {} }\n");
        var operation = new SearchSymbolsOperation(workspace.Context);

        var uncappedAll = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Widget" });
        Assert.True(uncappedAll.Success);
        Assert.Equal(5, uncappedAll.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ProjectPath = workspace.LibProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Symbols.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Symbols, s => Assert.Equal(workspace.LibSourcePath, s.File));
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathWithSourceFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);

        var otherProject = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "SharedName",
            ProjectPath = workspace.LibProjectPath,
            SourceFile = workspace.AppSourcePath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.Symbols);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);

        var sameProject = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "SharedName",
            ProjectPath = workspace.AppProjectPath,
            SourceFile = workspace.AppSourcePath
        });

        Assert.True(sameProject.Success);
        var symbol = Assert.Single(sameProject.Data!.Symbols);
        Assert.Equal("App.SharedName", symbol.FullyQualifiedName);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathWithKindAndNamespaceFilters_AndsTheFilters()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new SearchSymbolsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Speak",
            KindFilter = "Method",
            NamespaceFilter = "Lib",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(result.Success);
        var symbol = Assert.Single(result.Data!.Symbols);
        Assert.Equal("Speak", symbol.Name);

        var wrongNamespace = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Speak",
            NamespaceFilter = "App",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(wrongNamespace.Success);
        Assert.Empty(wrongNamespace.Data!.Symbols);
    }

    [SkippableFact]
    public async Task SearchSymbols_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespaceFilterSource);
        var operation = new SearchSymbolsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new SearchSymbolsParams { Query = "Widget" });
        var scoped = await operation.ExecuteAsync(new SearchSymbolsParams
        {
            Query = "Widget",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.True(scoped.Data!.TotalCount >= 2);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(
            omitted.Data.Symbols.Select(s => s.FullyQualifiedName),
            scoped.Data.Symbols.Select(s => s.FullyQualifiedName));
    }

    #endregion


    private static string FlipAsciiCase(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i]) ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
                break;
            }
        }
        return new string(chars);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateMultiFileAsync((fileName, source));

        public static Task<TempWorkspace> CreateMultiFileAsync(params (string FileName, string Source)[] files) =>
            CreateMultiFileCoreAsync(files);

        private static async Task<TempWorkspace> CreateMultiFileCoreAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbols_" + Guid.NewGuid().ToString("N"));
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

            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? firstPath = null;
            foreach (var (fileName, source) in files)
            {
                var relative = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(directory, relative);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, source);
                sourcePaths[fileName] = path;
                firstPath ??= path;
            }

            firstPath ??= Path.Combine(directory, "Foo.cs");

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(firstPath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {firstPath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = firstPath,
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

    private sealed class TwoProjectWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string LibProjectPath { get; init; }
        public required string AppProjectPath { get; init; }
        public required string LibSourcePath { get; init; }
        public required string AppSourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(
            string libSource = "namespace Lib { public class Animal { public void Speak() { } } public class SharedName {} }\n",
            string appSource = "namespace App { public class Dog : Lib.Animal {} public class SharedName {} }\n")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpSearchSymbolsXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSourcePath = Path.Combine(libDir, "Lib.cs");
            var appSourcePath = Path.Combine(appDir, "App.cs");

            await File.WriteAllTextAsync(libProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(appProject, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(libSourcePath, libSource);
            await File.WriteAllTextAsync(appSourcePath, appSource);

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
                if (context.GetDocumentByPath(libSourcePath) == null || context.GetDocumentByPath(appSourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibSourcePath = libSourcePath,
                    AppSourcePath = appSourcePath,
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

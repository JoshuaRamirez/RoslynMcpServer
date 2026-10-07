using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GoToDefinitionOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="GetTypeHierarchyOperation"/> / <see cref="FindImplementationsOperation"/>)
/// and optional <c>definitionFile</c> (restricts reported Definitions to one file; peer of
/// <c>get_type_hierarchy</c> <c>derivedFile</c> / <c>find_implementations</c> <c>implementationFile</c>),
/// and optional <c>projectPath</c> (restricts reported Definitions to one project; peer of
/// <c>get_diagnostics</c> / <c>search_symbols</c> / <c>find_references</c> / <c>find_callers</c> /
/// <c>find_implementations</c> / <c>get_type_hierarchy</c> <c>projectPath</c>).
/// </summary>
public class GoToDefinitionOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GoToDefinitionParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GoToDefinitionParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    // Partial-class parts are resolved by position (line 1, column 15 = "Widget" in the first part);
    // name-based resolution reports each partial declaration as a separate candidate.

    [SkippableFact]
    public async Task GoToDefinition_OmittedMaxResults_ReturnsAllPartialLocationsNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.Equal(result.Data.TotalCount, result.Data.Definitions.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.Equal(3, full.Data.TotalCount);

        var capped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Definitions.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(full.Data.Definitions[i].File, capped.Data.Definitions[i].File);
            Assert.Equal(full.Data.Definitions[i].Line, capped.Data.Definitions[i].Line);
            Assert.Equal(full.Data.Definitions[i].Column, capped.Data.Definitions[i].Column);
        }
    }

    [SkippableFact]
    public async Task GoToDefinition_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            partial class Widget { public int A; }
            partial class Widget { public int B; }
            partial class Widget { public int C; }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var atTotal = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 3
        });

        Assert.True(atTotal.Success);
        Assert.NotNull(atTotal.Data);
        Assert.Equal(3, atTotal.Data.TotalCount);
        Assert.Equal(3, atTotal.Data.Definitions.Count);
        Assert.False(atTotal.Data.Truncated);

        var above = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 1,
            Column = 15,
            MaxResults = 50
        });

        Assert.True(above.Success);
        Assert.NotNull(above.Data);
        Assert.Equal(3, above.Data.TotalCount);
        Assert.Equal(3, above.Data.Definitions.Count);
        Assert.False(above.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_SingleDefinition_MaxResultsOne_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            class Solo { }
            """);

        var operation = new GoToDefinitionOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Solo",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Single(result.Data.Definitions);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region definitionFile filter

    // Partial class Customer is declared in Customer.cs (line 1, column 22), twice in
    // Customer.Generated.cs (lines 2 and 3), and once in Customer.Validation.cs (line 1).
    // Partial method OnSaved is declared in Customer.cs (line 3, column 18) and implemented in
    // Customer.Validation.cs (line 3, column 18). Solo is a non-partial type in Solo.cs.
    // Empty.cs has no part of Customer.
    private static readonly Dictionary<string, string> MultiFileSources = new()
    {
        ["Customer.cs"] = """
            public partial class Customer
            {
                partial void OnSaved();
            }
            """,
        ["Customer.Generated.cs"] = """
            // generated parts
            public partial class Customer { public int Id; }
            public partial class Customer { public string? Name; }
            """,
        ["Customer.Validation.cs"] = """
            public partial class Customer
            {
                partial void OnSaved() { }
            }
            """,
        ["Solo.cs"] = """
            public class Solo { }
            """,
        ["Empty.cs"] = """
            public class Unrelated
            {
                public void Nothing() { }
            }
            """
    };

    [Fact]
    public void DefinitionFile_DefaultsToNull()
    {
        var @params = new GoToDefinitionParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.DefinitionFile);
    }

    [SkippableFact]
    public async Task GoToDefinition_OmittedDefinitionFile_ReturnsDefinitionsFromAllFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(4, result.Data.Definitions.Count);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(result.Data.Definitions, d => SamePath(d.File, workspace.PathOf("Customer.cs")));
        Assert.Equal(2, result.Data.Definitions.Count(d => SamePath(d.File, workspace.PathOf("Customer.Generated.cs"))));
        Assert.Contains(result.Data.Definitions, d => SamePath(d.File, workspace.PathOf("Customer.Validation.cs")));
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialTypeFiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("Customer.Generated.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Definitions.Count);
        Assert.All(result.Data.Definitions, d => Assert.True(SamePath(d.File, workspace.PathOf("Customer.Generated.cs"))));
        Assert.All(result.Data.Definitions, d => Assert.Equal("Customer", d.SymbolName));
        Assert.Equal(new[] { 2, 3 }, result.Data.Definitions.Select(d => d.Line).OrderBy(l => l));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_LineColumnResolutionFromOtherFile_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Resolve Customer at its Customer.Validation.cs part and ask only for the Customer.cs part.
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.Validation.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("Customer.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Definitions);
        Assert.Equal("Customer", only.SymbolName);
        Assert.True(SamePath(only.File, workspace.PathOf("Customer.cs")));
        Assert.Equal(1, only.Line);
        Assert.Equal(22, only.Column);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialMethodFiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var inFile = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            DefinitionFile = workspace.PathOf("Customer.cs")
        });

        Assert.True(inFile.Success);
        Assert.NotNull(inFile.Data);
        var only = Assert.Single(inFile.Data.Definitions);
        Assert.Equal("OnSaved", only.SymbolName);
        Assert.True(SamePath(only.File, workspace.PathOf("Customer.cs")));
        Assert.Equal(3, only.Line);
        Assert.Equal(1, inFile.Data.TotalCount);
        Assert.False(inFile.Data.Truncated);

        var elsewhere = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            DefinitionFile = workspace.PathOf("Customer.Generated.cs")
        });

        Assert.True(elsewhere.Success);
        Assert.NotNull(elsewhere.Data);
        Assert.Empty(elsewhere.Data.Definitions);
        Assert.Equal(0, elsewhere.Data.TotalCount);
        Assert.False(elsewhere.Data.Truncated);

        // The implementation half lives in a distinct linked IMethodSymbol; filtering to its file
        // must still reach it (previously masked: only the defining file and an unrelated file
        // were checked).
        var implementationFile = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            DefinitionFile = workspace.PathOf("Customer.Validation.cs")
        });

        Assert.True(implementationFile.Success);
        Assert.NotNull(implementationFile.Data);
        var implementation = Assert.Single(implementationFile.Data.Definitions);
        Assert.Equal("OnSaved", implementation.SymbolName);
        Assert.True(SamePath(implementation.File, workspace.PathOf("Customer.Validation.cs")));
        Assert.Equal(3, implementation.Line);
        Assert.Equal(1, implementationFile.Data.TotalCount);
        Assert.False(implementationFile.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialMethodFromDefinition_FiltersToImplementationFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Resolve OnSaved at its defining declaration (Customer.cs) and ask for the implementation file.
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            DefinitionFile = workspace.PathOf("Customer.Validation.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Definitions);
        Assert.Equal("OnSaved", only.SymbolName);
        Assert.True(SamePath(only.File, workspace.PathOf("Customer.Validation.cs")));
        Assert.Equal(3, only.Line);
        Assert.Equal(18, only.Column);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialMethodFromImplementation_FiltersToDefiningFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Resolve OnSaved at its implementation (Customer.Validation.cs) and ask for the defining file.
        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.Validation.cs"),
            Line = 3,
            Column = 18,
            DefinitionFile = workspace.PathOf("Customer.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Definitions);
        Assert.Equal("OnSaved", only.SymbolName);
        Assert.True(SamePath(only.File, workspace.PathOf("Customer.cs")));
        Assert.Equal(3, only.Line);
        Assert.Equal(18, only.Column);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialMethodUnrelatedFile_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        foreach (var (sourceFile, line) in new[] { ("Customer.cs", 3), ("Customer.Validation.cs", 3) })
        {
            var result = await operation.ExecuteAsync(new GoToDefinitionParams
            {
                SourceFile = workspace.PathOf(sourceFile),
                Line = line,
                Column = 18,
                DefinitionFile = workspace.PathOf("Empty.cs")
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Data);
            Assert.Empty(result.Data.Definitions);
            Assert.Equal(0, result.Data.TotalCount);
            Assert.False(result.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GoToDefinition_OmittedDefinitionFile_PartialMethodUnchanged()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Without definitionFile the reported set is the resolved half's own locations (unchanged).
        var fromDefinition = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18
        });

        Assert.True(fromDefinition.Success);
        Assert.NotNull(fromDefinition.Data);
        Assert.True(SamePath(Assert.Single(fromDefinition.Data.Definitions).File, workspace.PathOf("Customer.cs")));

        var fromImplementation = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.Validation.cs"),
            Line = 3,
            Column = 18
        });

        Assert.True(fromImplementation.Success);
        Assert.NotNull(fromImplementation.Data);
        Assert.True(SamePath(Assert.Single(fromImplementation.Data.Definitions).File, workspace.PathOf("Customer.Validation.cs")));
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialProperty_ReachesOtherPart()
    {
        // Partial properties (C# 13): defining declaration in Order.cs, implementation in
        // Order.Impl.cs (both line 3, column 24 = "Total").
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Order.cs"] = """
                public partial class Order
                {
                    public partial int Total { get; }
                }
                """,
            ["Order.Impl.cs"] = """
                public partial class Order
                {
                    public partial int Total => 42;
                }
                """
        });
        var operation = new GoToDefinitionOperation(workspace.Context);

        var toImplementation = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Order.cs"),
            Line = 3,
            Column = 24,
            DefinitionFile = workspace.PathOf("Order.Impl.cs")
        });

        Assert.True(toImplementation.Success);
        Assert.NotNull(toImplementation.Data);
        var implementation = Assert.Single(toImplementation.Data.Definitions);
        Assert.Equal("Total", implementation.SymbolName);
        Assert.True(SamePath(implementation.File, workspace.PathOf("Order.Impl.cs")));
        Assert.Equal(3, implementation.Line);
        Assert.Equal(1, toImplementation.Data.TotalCount);

        var toDefinition = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Order.Impl.cs"),
            Line = 3,
            Column = 24,
            DefinitionFile = workspace.PathOf("Order.cs")
        });

        Assert.True(toDefinition.Success);
        Assert.NotNull(toDefinition.Data);
        var definition = Assert.Single(toDefinition.Data.Definitions);
        Assert.Equal("Total", definition.SymbolName);
        Assert.True(SamePath(definition.File, workspace.PathOf("Order.cs")));
        Assert.Equal(3, definition.Line);
        Assert.Equal(1, toDefinition.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_PartialEvent_ReachesOtherPart()
    {
        // Partial events (C# 14, so LangVersion preview on the net9.0 test project): defining
        // declaration in Bus.cs, implementation in Bus.Impl.cs (both line 3, column 41 = "Changed").
        // IEventSymbol.PartialDefinitionPart / PartialImplementationPart is a distinct Roslyn API
        // path from partial methods and properties.
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Bus.cs"] = """
                public partial class Bus
                {
                    public partial event System.Action? Changed;
                }
                """,
            ["Bus.Impl.cs"] = """
                public partial class Bus
                {
                    public partial event System.Action? Changed { add { } remove { } }
                }
                """
        }, langVersion: "preview");
        var operation = new GoToDefinitionOperation(workspace.Context);

        var toImplementation = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Bus.cs"),
            Line = 3,
            Column = 41,
            DefinitionFile = workspace.PathOf("Bus.Impl.cs")
        });

        Assert.True(toImplementation.Success);
        Assert.NotNull(toImplementation.Data);
        var implementation = Assert.Single(toImplementation.Data.Definitions);
        Assert.Equal("Changed", implementation.SymbolName);
        Assert.True(SamePath(implementation.File, workspace.PathOf("Bus.Impl.cs")));
        Assert.Equal(3, implementation.Line);
        Assert.Equal(1, toImplementation.Data.TotalCount);

        var toDefinition = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Bus.Impl.cs"),
            Line = 3,
            Column = 41,
            DefinitionFile = workspace.PathOf("Bus.cs")
        });

        Assert.True(toDefinition.Success);
        Assert.NotNull(toDefinition.Data);
        var definition = Assert.Single(toDefinition.Data.Definitions);
        Assert.Equal("Changed", definition.SymbolName);
        Assert.True(SamePath(definition.File, workspace.PathOf("Bus.cs")));
        Assert.Equal(3, definition.Line);
        Assert.Equal(1, toDefinition.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_SymbolNameResolution_FiltersToThatFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Solo.cs"),
            SymbolName = "Solo",
            DefinitionFile = workspace.PathOf("Solo.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Definitions);
        Assert.True(SamePath(only.File, workspace.PathOf("Solo.cs")));
        Assert.Equal(1, only.Line);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFileWithNoDefinitionPart_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("Empty.cs")
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Definitions);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFile_ExcludesMetadataDefinition()
    {
        await using var workspace = await TempWorkspace.CreateAsync(new Dictionary<string, string>
        {
            ["Uses.cs"] = """
                public class Uses
                {
                    public string Text = "";
                }
                """
        });
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Line 3, column 12 = "string" (System.String, a metadata symbol).
        var unfiltered = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Uses.cs"),
            Line = 3,
            Column = 12
        });

        Assert.True(unfiltered.Success);
        Assert.NotNull(unfiltered.Data);
        Assert.Equal("(metadata)", Assert.Single(unfiltered.Data.Definitions).File);

        var filtered = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Uses.cs"),
            Line = 3,
            Column = 12,
            DefinitionFile = workspace.PathOf("Uses.cs")
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.Empty(filtered.Data.Definitions);
        Assert.Equal(0, filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFileWithMaxResults_CapsFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("Customer.Generated.cs"),
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var only = Assert.Single(result.Data.Definitions);
        Assert.True(SamePath(only.File, workspace.PathOf("Customer.Generated.cs")));
        // TotalCount reflects the filtered set (2 in Customer.Generated.cs), not the overall 4.
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_DefinitionFileWithMaxResultsAboveFilteredCount_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("Customer.Validation.cs"),
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data.Definitions);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_NonexistentDefinitionFile_ReturnsSourceFileNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = workspace.PathOf("DoesNotExist.cs")
        }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GoToDefinition_RelativeDefinitionFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = "Customer.Generated.cs"
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GoToDefinition_NonCsDefinitionFile_ReturnsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);
        var textFile = workspace.PathOf("notes.txt");
        await File.WriteAllTextAsync(textFile, "not C#");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = textFile
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GoToDefinition_AliasedDefinitionFilePath_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);
        Directory.CreateDirectory(workspace.PathOf("sub"));
        var aliased = Path.Combine(workspace.DirectoryPath, "sub", "..", "Customer.Generated.cs");

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = aliased
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Definitions.Count);
        Assert.All(result.Data.Definitions, d => Assert.True(SamePath(d.File, workspace.PathOf("Customer.Generated.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_CaseOnlyDifferentDefinitionFile_StillMatches()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);
        var caseVariant = workspace.PathOf("CUSTOMER.GENERATED.cs");

        // On case-insensitive volumes (Windows / default macOS) the upper-cased path already
        // resolves to Customer.Generated.cs. On case-sensitive volumes (Linux) definitionFile must
        // exist to pass validation (same as get_type_hierarchy derivedFile), so create a
        // declaration-free stub at the case-variant path; the documented OrdinalIgnoreCase
        // comparison must still match Customer.Generated.cs's definition parts.
        if (!File.Exists(caseVariant))
            await File.WriteAllTextAsync(caseVariant, "// case-variant stub with no declarations");

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            DefinitionFile = caseVariant
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.Definitions.Count);
        Assert.All(result.Data.Definitions, d => Assert.True(SamePath(d.File, workspace.PathOf("Customer.Generated.cs"))));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region projectPath filter

    // Two-project scenario: Lib/Widget.cs declares partial Widget (line 1, column 22) and
    // Lib/Widget.Part.cs adds two more parts (lines 1 and 2). App (which references Lib) uses Widget
    // in App/Uses.cs (line 3, column 12) and string (line 4, column 12; metadata), and declares its
    // own partial AppOnly in App/AppOnly.cs (lines 1 and 2, column 22) and App/AppOnly.Part.cs
    // (line 1).
    private const string LibWidgetSource = """
        public partial class Widget { }
        """;

    private const string LibWidgetPartSource = """
        public partial class Widget { public int A; }
        public partial class Widget { public int B; }
        """;

    private const string AppUsesSource = """
        public class Uses
        {
            public Widget W = new();
            public string Text = "";
        }
        """;

    private const string AppOnlySource = """
        public partial class AppOnly { }
        public partial class AppOnly { public int X; }
        """;

    private const string AppOnlyPartSource = """
        public partial class AppOnly { public int Y; }
        """;

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new GoToDefinitionParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
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
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            ProjectPath = projectPath
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            ProjectPath = workspace.PathOf("Customer.cs")
        }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = missing
        }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathOmittedOrBlank_ReturnsEveryDefinition()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12
        });
        var blank = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = "  "
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(3, omitted.Data!.TotalCount);
        Assert.Equal(3, omitted.Data.Definitions.Count);
        Assert.False(omitted.Data.Truncated);
        Assert.Equal(Sites(omitted.Data.Definitions), Sites(blank.Data!.Definitions));
        Assert.Equal(omitted.Data.TotalCount, blank.Data.TotalCount);
        Assert.False(blank.Data.Truncated);

        // Metadata fallback is unchanged when projectPath is omitted or blank.
        var metadataBlank = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 4,
            Column = 12,
            ProjectPath = ""
        });

        Assert.True(metadataBlank.Success);
        Assert.Equal("(metadata)", Assert.Single(metadataBlank.Data!.Definitions).File);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        // sourceFile (App) still locates Widget; projectPath (Lib) scopes reported Definitions to
        // Widget's three partial parts in Lib's documents.
        var lib = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(3, lib.Data!.Definitions.Count);
        Assert.Equal(3, lib.Data.TotalCount);
        Assert.False(lib.Data.Truncated);
        Assert.All(lib.Data.Definitions, d => Assert.Equal("Widget", d.SymbolName));
        Assert.Single(lib.Data.Definitions, d => SamePath(d.File, workspace.LibWidgetPath));
        Assert.Equal(2, lib.Data.Definitions.Count(d => SamePath(d.File, workspace.LibWidgetPartPath)));

        // Widget has no definition in App: empty success, not an error and not the metadata fallback.
        var app = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Empty(app.Data!.Definitions);
        Assert.Equal(0, app.Data.TotalCount);
        Assert.False(app.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_SymbolNameResolution_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            SymbolName = "Uses",
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        var only = Assert.Single(app.Data!.Definitions);
        Assert.True(SamePath(only.File, workspace.AppUsesPath));
        Assert.Equal(1, app.Data.TotalCount);

        var other = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            SymbolName = "Uses",
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(other.Success);
        Assert.Empty(other.Data!.Definitions);
        Assert.Equal(0, other.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_PartialTypeAcrossProjectFiles_ReportsEveryPartInProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        // AppOnly is split across App/AppOnly.cs (two parts) and App/AppOnly.Part.cs (one part).
        var app = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppOnlyPath,
            Line = 1,
            Column = 22,
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Equal(3, app.Data!.TotalCount);
        Assert.Equal(new[] { 1, 2 }, app.Data.Definitions.Where(d => SamePath(d.File, workspace.AppOnlyPath)).Select(d => d.Line).OrderBy(l => l));
        Assert.Equal(1, Assert.Single(app.Data.Definitions, d => SamePath(d.File, workspace.AppOnlyPartPath)).Line);
        Assert.False(app.Data.Truncated);

        var lib = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppOnlyPath,
            Line = 1,
            Column = 22,
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Empty(lib.Data!.Definitions);
        Assert.Equal(0, lib.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathWithDefinitionFile_AndsTheScopes()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        var sameProject = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = workspace.LibProjectPath,
            DefinitionFile = workspace.LibWidgetPartPath
        });

        Assert.True(sameProject.Success);
        Assert.Equal(2, sameProject.Data!.Definitions.Count);
        Assert.All(sameProject.Data.Definitions, d => Assert.True(SamePath(d.File, workspace.LibWidgetPartPath)));
        Assert.Equal(new[] { 1, 2 }, sameProject.Data.Definitions.Select(d => d.Line).OrderBy(l => l));
        Assert.Equal(2, sameProject.Data.TotalCount);
        Assert.False(sameProject.Data.Truncated);

        // definitionFile in Lib but projectPath App: no location satisfies both.
        var otherProject = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = workspace.AppProjectPath,
            DefinitionFile = workspace.LibWidgetPartPath
        });

        Assert.True(otherProject.Success);
        Assert.Empty(otherProject.Data!.Definitions);
        Assert.Equal(0, otherProject.Data.TotalCount);
        Assert.False(otherProject.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathWithMaxResults_CapsProjectScopedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppOnlyPath,
            Line = 1,
            Column = 22,
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.Definitions.Count);
        Assert.All(capped.Data.Definitions, d => Assert.True(
            SamePath(d.File, workspace.AppOnlyPath) || SamePath(d.File, workspace.AppOnlyPartPath)));
        Assert.Equal(3, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var exact = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppOnlyPath,
            Line = 1,
            Column = 22,
            ProjectPath = workspace.AppProjectPath,
            MaxResults = 3
        });

        Assert.True(exact.Success);
        Assert.Equal(3, exact.Data!.Definitions.Count);
        Assert.Equal(3, exact.Data.TotalCount);
        Assert.False(exact.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathWithDefinitionFileAndMaxResults_CapsCombinedSet()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = workspace.LibProjectPath,
            DefinitionFile = workspace.LibWidgetPartPath,
            MaxResults = 1
        });

        Assert.True(result.Success);
        var only = Assert.Single(result.Data!.Definitions);
        Assert.True(SamePath(only.File, workspace.LibWidgetPartPath));
        Assert.Equal(2, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_ExcludesMetadataDefinition()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);

        // Line 4, column 12 = "string" (System.String, a metadata symbol).
        var unfiltered = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 4,
            Column = 12
        });

        Assert.True(unfiltered.Success);
        Assert.Equal("(metadata)", Assert.Single(unfiltered.Data!.Definitions).File);

        foreach (var projectPath in new[] { workspace.AppProjectPath, workspace.LibProjectPath })
        {
            var scoped = await operation.ExecuteAsync(new GoToDefinitionParams
            {
                SourceFile = workspace.AppUsesPath,
                Line = 4,
                Column = 12,
                ProjectPath = projectPath
            });

            Assert.True(scoped.Success);
            Assert.Empty(scoped.Data!.Definitions);
            Assert.Equal(0, scoped.Data.TotalCount);
            Assert.False(scoped.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GoToDefinitionOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.LibProjectPath)!, "LIB.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new GoToDefinitionParams
        {
            SourceFile = workspace.AppUsesPath,
            Line = 3,
            Column = 12,
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.Equal(3, result.Data!.TotalCount);
        }
        else
        {
            // Case-sensitive volume (Linux): LIB.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathLinkedFile_ReportsOnlyThatProjectsDocument()
    {
        // Linked.cs is compiled into both Lib and App (internal, so the two copies do not clash). Lib's
        // Shared is partial across Linked.cs and Lib/Shared.Lib.cs (line 1, column 24); App's copy of
        // Linked.cs declares a distinct Shared symbol, so scoping Lib's Shared to App is empty.
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            linkedSource: "internal partial class Shared { public int L; }",
            libSharedSource: "internal partial class Shared { public int M; }");
        var operation = new GoToDefinitionOperation(workspace.Context);

        var lib = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.LibSharedPath!,
            Line = 1,
            Column = 24,
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.Equal(2, lib.Data!.TotalCount);
        Assert.Single(lib.Data.Definitions, d => SamePath(d.File, workspace.LinkedPath!));
        Assert.Single(lib.Data.Definitions, d => SamePath(d.File, workspace.LibSharedPath!));

        var app = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.LibSharedPath!,
            Line = 1,
            Column = 24,
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.Empty(app.Data!.Definitions);
        Assert.Equal(0, app.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22
        });
        var scoped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(4, scoped.Data!.TotalCount);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(Sites(omitted.Data.Definitions), Sites(scoped.Data.Definitions));
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_PartialMethod_UnchangedAloneAndReachesOtherHalfWithDefinitionFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MultiFileSources);
        var operation = new GoToDefinitionOperation(workspace.Context);

        // projectPath alone keeps the resolved half's own locations (same as omitted).
        var alone = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(alone.Success);
        Assert.True(SamePath(Assert.Single(alone.Data!.Definitions).File, workspace.PathOf("Customer.cs")));

        // Combined with definitionFile, the other half's file is still reachable.
        var combined = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 3,
            Column = 18,
            ProjectPath = workspace.ProjectPath,
            DefinitionFile = workspace.PathOf("Customer.Validation.cs")
        });

        Assert.True(combined.Success);
        var implementation = Assert.Single(combined.Data!.Definitions);
        Assert.Equal("OnSaved", implementation.SymbolName);
        Assert.True(SamePath(implementation.File, workspace.PathOf("Customer.Validation.cs")));
        Assert.Equal(1, combined.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPathMultiTargeted_IncludesEveryTargetFramework()
    {
        // Every target-framework variant of a multi-targeted project shares its .csproj path, so
        // the definitions are reported whichever variant the symbol was resolved in.
        await using var workspace = await TempWorkspace.CreateAsync(
            MultiFileSources, targetFrameworks: "net8.0;net9.0");
        var variants = workspace.Context.Solution.Projects.Count(p =>
            p.FilePath != null && SamePath(p.FilePath, workspace.ProjectPath));
        Skip.If(variants < 2, "MSBuild did not load one project per target framework.");

        var operation = new GoToDefinitionOperation(workspace.Context);
        var omitted = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22
        });
        var scoped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.PathOf("Customer.cs"),
            Line = 1,
            Column = 22,
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.Equal(4, scoped.Data!.TotalCount);
        Assert.Equal(Sites(omitted.Data!.Definitions), Sites(scoped.Data.Definitions));
    }

    [SkippableFact]
    public async Task GoToDefinition_ProjectPath_IncludesSourceGeneratedDefinitions()
    {
        // [GeneratedRegex] makes the BCL regex source generator emit a second `partial class Rx`
        // part into a generated tree. That tree belongs to the project's compilation but is not a
        // regular document, so projectPath must still keep it (same set as the unfiltered call).
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text.RegularExpressions;
            public static partial class Rx
            {
                [GeneratedRegex("ab+")]
                public static partial Regex Ab();
            }
            """, "Rx.cs");
        var operation = new GoToDefinitionOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 2,
            Column = 29
        });

        Assert.True(omitted.Success);
        Assert.All(omitted.Data!.Definitions, d => Assert.Equal("Rx", d.SymbolName));
        Skip.If(
            omitted.Data.Definitions.All(d => SamePath(d.File, workspace.SourcePath)),
            "The regex source generator did not run in this MSBuild workspace.");

        var scoped = await operation.ExecuteAsync(new GoToDefinitionParams
        {
            SourceFile = workspace.SourcePath,
            Line = 2,
            Column = 29,
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(scoped.Success);
        Assert.Equal(omitted.Data.TotalCount, scoped.Data!.TotalCount);
        Assert.Equal(Sites(omitted.Data.Definitions), Sites(scoped.Data.Definitions));
        Assert.Contains(scoped.Data.Definitions, d => !SamePath(d.File, workspace.SourcePath));
    }

    #endregion

    private static List<(string File, int Line, int Column)> Sites(IEnumerable<DefinitionLocation> definitions) =>
        definitions
            .Select(d => (d.File, d.Line, d.Column))
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ThenBy(s => s.Column)
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

        public static async Task<TempWorkspace> CreateAsync(
            IReadOnlyDictionary<string, string> files, string? primaryFile = null, string? langVersion = null,
            string? targetFrameworks = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGoToDefinition_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var langVersionElement = langVersion == null ? "" : $"<LangVersion>{langVersion}</LangVersion>";
            var targetFrameworkElement = targetFrameworks == null
                ? "<TargetFramework>net9.0</TargetFramework>"
                : $"<TargetFrameworks>{targetFrameworks}</TargetFrameworks>";
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    {targetFrameworkElement}
                    <Nullable>enable</Nullable>
                    {langVersionElement}
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
        public required string LibWidgetPath { get; init; }
        public required string LibWidgetPartPath { get; init; }
        public required string AppUsesPath { get; init; }
        public required string AppOnlyPath { get; init; }
        public required string AppOnlyPartPath { get; init; }
        public string? LinkedPath { get; init; }
        public string? LibSharedPath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TwoProjectWorkspace> CreateAsync(string? linkedSource = null, string? libSharedSource = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGoToDefinitionXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libWidget = Path.Combine(libDir, "Widget.cs");
            var libWidgetPart = Path.Combine(libDir, "Widget.Part.cs");
            var appUses = Path.Combine(appDir, "Uses.cs");
            var appOnly = Path.Combine(appDir, "AppOnly.cs");
            var appOnlyPart = Path.Combine(appDir, "AppOnly.Part.cs");

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

            string? libSharedPath = null;
            if (libSharedSource != null)
            {
                libSharedPath = Path.Combine(libDir, "Shared.Lib.cs");
                await File.WriteAllTextAsync(libSharedPath, libSharedSource);
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
            await File.WriteAllTextAsync(libWidget, LibWidgetSource);
            await File.WriteAllTextAsync(libWidgetPart, LibWidgetPartSource);
            await File.WriteAllTextAsync(appUses, AppUsesSource);
            await File.WriteAllTextAsync(appOnly, AppOnlySource);
            await File.WriteAllTextAsync(appOnlyPart, AppOnlyPartSource);

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
                if (context.GetDocumentByPath(libWidget) == null ||
                    context.GetDocumentByPath(libWidgetPart) == null ||
                    context.GetDocumentByPath(appUses) == null ||
                    context.GetDocumentByPath(appOnly) == null ||
                    context.GetDocumentByPath(appOnlyPart) == null ||
                    (libSharedPath != null && context.GetDocumentByPath(libSharedPath) == null))
                {
                    context.Dispose();
                    throw new InvalidOperationException("Workspace loaded but did not include Lib/App sources.");
                }

                return new TwoProjectWorkspace
                {
                    DirectoryPath = directory,
                    LibProjectPath = libProject,
                    AppProjectPath = appProject,
                    LibWidgetPath = libWidget,
                    LibWidgetPartPath = libWidgetPart,
                    AppUsesPath = appUses,
                    AppOnlyPath = appOnly,
                    AppOnlyPartPath = appOnlyPart,
                    LinkedPath = linkedPath,
                    LibSharedPath = libSharedPath,
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

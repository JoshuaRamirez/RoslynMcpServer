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
/// <c>get_type_hierarchy</c> <c>derivedFile</c> / <c>find_implementations</c> <c>implementationFile</c>).
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

    private static bool SamePath(string? a, string b) =>
        a != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs") =>
            CreateAsync(new Dictionary<string, string> { [fileName] = source }, fileName);

        public static async Task<TempWorkspace> CreateAsync(
            IReadOnlyDictionary<string, string> files, string? primaryFile = null, string? langVersion = null)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGoToDefinition_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var langVersionElement = langVersion == null ? "" : $"<LangVersion>{langVersion}</LangVersion>";
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
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
}

using System.Text.Json;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDocumentOutlineOperation"/> optional <c>maxResults</c>
/// (peer of <see cref="GetDiagnosticsOperation"/> / <see cref="SearchSymbolsOperation"/>), optional
/// <c>maxDepth</c>, optional <c>kindFilter</c>, and optional <c>nameFilter</c>.
/// </summary>
public class GetDocumentOutlineOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.MaxResults);
    }

    [Fact]
    public void MaxDepth_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.MaxDepth);
    }

    [SkippableFact]
    public async Task Validate_MaxDepthZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("namespace N { class C { } }\n");
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxDepth = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxDepth must be >= 1.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("namespace N { class C { } }\n");
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxResults = 0
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetDocumentOutline_OmittedMaxResults_ReturnsFullTreeNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public int Prop { get; set; }
                    public void M() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 4, $"Expected >= 4 outline nodes, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, CountNodes(result.Data.Entries));
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public int Prop { get; set; }
                    public void M() { }
                    public void N2() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 5);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxResults = 3
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(3, CountNodes(capped.Data.Entries));
        Assert.True(capped.Data.Truncated);

        // Deterministic DFS pre-order prefix of today's tree:
        // N (ns) -> C (class) -> Prop (first member)
        Assert.Single(capped.Data.Entries);
        Assert.Equal("N", capped.Data.Entries[0].Name);
        Assert.Equal("Namespace", capped.Data.Entries[0].Kind);
        Assert.NotNull(capped.Data.Entries[0].Children);
        Assert.Single(capped.Data.Entries[0].Children!);
        Assert.Equal("C", capped.Data.Entries[0].Children![0].Name);
        Assert.Equal("Class", capped.Data.Entries[0].Children![0].Kind);
        Assert.NotNull(capped.Data.Entries[0].Children![0].Children);
        Assert.Single(capped.Data.Entries[0].Children![0].Children!);
        Assert.Equal(full.Data.Entries[0].Children![0].Children![0].Name,
            capped.Data.Entries[0].Children![0].Children![0].Name);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace N
            {
                public class C
                {
                    public void M() { }
                }
            }
            """);

        var operation = new GetDocumentOutlineOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, CountNodes(capped.Data.Entries));
        Assert.False(capped.Data.Truncated);
    }

    #endregion

    #region Execute maxDepth

    private const string DepthSource = """
        namespace N
        {
            public class A
            {
                public int P { get; set; }
                public void M() { }

                public class Inner
                {
                    public void InnerM() { }
                }
            }

            public class B
            {
                public void BM() { }
            }

            public enum E
            {
                One,
                Two
            }
        }
        """;

    [SkippableFact]
    public async Task GetDocumentOutline_OmittedMaxDepth_MatchesTodayFullTree()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        // N, A, Inner, InnerM, M, P, B, BM, E, One, Two => 11 nodes; deepest path N > A > Inner > InnerM
        Assert.Equal(11, full.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, CountNodes(full.Data.Entries));
        Assert.Equal(4, MaxTreeDepth(full.Data.Entries));
        Assert.False(full.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthOne_ReturnsRootsOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(11, capped.Data.TotalCount);
        var root = Assert.Single(capped.Data.Entries);
        Assert.Equal("N", root.Name);
        Assert.Equal("Namespace", root.Kind);
        Assert.Null(root.Children);
        Assert.True(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthTwo_ReturnsAllTopLevelTypesWithoutMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        var root = Assert.Single(capped.Data.Entries);
        Assert.NotNull(root.Children);
        Assert.Equal(
            full.Data.Entries[0].Children!.Select(c => (c.Name, c.Kind, c.Line, c.Column, c.Accessibility, c.ReturnType)),
            root.Children!.Select(c => (c.Name, c.Kind, c.Line, c.Column, c.Accessibility, c.ReturnType)));
        Assert.Equal(new[] { "A", "B", "E" }, root.Children!.Select(c => c.Name));
        Assert.All(root.Children!, c => Assert.Null(c.Children));
        Assert.Equal(4, CountNodes(capped.Data.Entries));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthAtOrAboveTreeDepth_ReturnsFullTreeNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);

        foreach (var maxDepth in new[] { 4, 10 })
        {
            var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                MaxDepth = maxDepth
            });

            Assert.True(capped.Success);
            Assert.NotNull(capped.Data);
            Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
            Assert.Equal(full.Data.TotalCount, CountNodes(capped.Data.Entries));
            Assert.Equal(Flatten(full.Data.Entries), Flatten(capped.Data.Entries));
            Assert.False(capped.Data.Truncated);
        }
    }

    [SkippableFact]
    public async Task GetDocumentOutline_MaxDepthWithMaxResults_AppliesDepthCapFirst()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DepthSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // Depth cap 2 keeps N, A, B, E (4 nodes); maxResults 3 then keeps the DFS pre-order prefix N, A, B.
        // Without the depth cap, maxResults 3 would be N, A, Inner (budget spent inside A).
        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2,
            MaxResults = 3
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(11, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(new[] { "N", "A", "B" }, Flatten(capped.Data.Entries).Select(e => e.Name));
        var root = Assert.Single(capped.Data.Entries);
        Assert.All(root.Children!, c => Assert.Null(c.Children));

        // maxResults at or above the depth-capped node count: only the depth cap applies.
        var depthOnly = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            MaxDepth = 2,
            MaxResults = 4
        });

        Assert.True(depthOnly.Success);
        Assert.NotNull(depthOnly.Data);
        Assert.Equal(11, depthOnly.Data.TotalCount);
        Assert.True(depthOnly.Data.Truncated);
        Assert.Equal(new[] { "N", "A", "B", "E" }, Flatten(depthOnly.Data.Entries).Select(e => e.Name));
    }

    #endregion

    #region Execute kindFilter

    private const string KindSource = """
        namespace N
        {
            public class A
            {
                public A() { }
                public int P { get; set; }
                public void M() { }

                public class Inner
                {
                    public void InnerM() { }
                }
            }

            public class B
            {
                public int F;
                public const int K = 1;
            }

            public enum E
            {
                One,
                Two
            }

            public interface I
            {
                void IM();
            }
        }
        """;

    // Full DFS pre-order: N, A, Inner, InnerM, A (ctor), M, P, B, F, K, E, One, Two, I, IM => 15 nodes.
    private const int KindSourceTotal = 15;

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetDocumentOutline_OmittedOrBlankKindFilter_MatchesTodayOutputExactly(string? kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var baseline = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var filtered = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });

        Assert.True(baseline.Success);
        Assert.NotNull(baseline.Data);
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.Equal(KindSourceTotal, filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal(SerializeOutline(baseline.Data), SerializeOutline(filtered.Data));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterMethod_KeepsMethodsAndTheirContainers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method"
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        var flat = Flatten(result.Data.Entries);
        Assert.Equal(
            new[]
            {
                ("N", "Namespace", 1),
                ("A", "Class", 2),
                ("Inner", "Class", 3),
                ("InnerM", "Method", 4),
                ("A", "Constructor", 3),
                ("M", "Method", 3),
                ("I", "Interface", 2),
                ("IM", "Method", 3)
            },
            flat.Select(e => (e.Name, e.Kind, e.Depth)));

        // Kept entries (containers included) keep their original positions and metadata.
        var original = Flatten(full.Data.Entries);
        Assert.All(flat, e => Assert.Contains(e, original));

        Assert.Equal(8, result.Data.TotalCount);
        Assert.Equal(result.Data.TotalCount, CountNodes(result.Data.Entries));
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterClass_KeepsOnlyTypeNodesAndNamespaceContainers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Class"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[]
            {
                ("N", "Namespace", 1),
                ("A", "Class", 2),
                ("Inner", "Class", 3),
                ("B", "Class", 2)
            },
            Flatten(result.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));

        var root = Assert.Single(result.Data.Entries);
        var b = root.Children!.Single(c => c.Name == "B");
        Assert.Null(b.Children);
        var inner = Assert.Single(root.Children!.Single(c => c.Name == "A").Children!);
        Assert.Null(inner.Children);

        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterConstant_IncludesConstFieldsAndEnumMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Constant"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[]
            {
                ("N", "Namespace", 1),
                ("B", "Class", 2),
                ("K", "Constant", 3),
                ("E", "Enum", 2),
                ("One", "EnumMember", 3),
                ("Two", "EnumMember", 3)
            },
            Flatten(result.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));
        Assert.Equal(6, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterNamespace_KeepsNamespaceWithoutChildren()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Namespace"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var root = Assert.Single(result.Data.Entries);
        Assert.Equal("N", root.Name);
        Assert.Null(root.Children);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterNoMatch_ReturnsEmptyOutline()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Delegate"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Entries);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("method")]
    [InlineData("METHOD")]
    [InlineData("mEtHoD")]
    public async Task GetDocumentOutline_KindFilter_IsCaseInsensitive(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var canonical = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method"
        });
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });

        Assert.True(canonical.Success);
        Assert.NotNull(canonical.Data);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(SerializeOutline(canonical.Data), SerializeOutline(result.Data));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterWithMaxDepth_AppliesFilterBeforeDepthCap()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // Method-filtered tree: N > [A > [Inner > [InnerM], A (ctor), M], I > [IM]] (8 nodes);
        // depth 2 keeps N, A, I. B and E were removed by the filter, not the depth cap.
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MaxDepth = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "N", "A", "I" }, Flatten(result.Data.Entries).Select(e => e.Name));
        var root = Assert.Single(result.Data.Entries);
        Assert.All(root.Children!, c => Assert.Null(c.Children));
        Assert.Equal(8, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterWithMaxResults_AppliesFilterBeforeNodeBudget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MaxResults = 5
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(new[] { "N", "A", "Inner", "InnerM", "A" }, Flatten(capped.Data.Entries).Select(e => e.Name));
        Assert.Equal(8, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        // maxResults at or above the filtered node count: nothing beyond the filter is dropped.
        var notCapped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MaxResults = 8
        });

        Assert.True(notCapped.Success);
        Assert.NotNull(notCapped.Data);
        Assert.Equal(8, CountNodes(notCapped.Data.Entries));
        Assert.Equal(8, notCapped.Data.TotalCount);
        Assert.False(notCapped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_KindFilterWithMaxDepthAndMaxResults_AppliesFilterThenDepthThenBudget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // filter (Method) -> depth 2 => N, A, I -> budget 2 => N, A.
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MaxDepth = 2,
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "N", "A" }, Flatten(result.Data.Entries).Select(e => e.Name));
        Assert.Equal(8, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Bogus")]
    [InlineData("Methods")]
    [InlineData("Constructor")]
    [InlineData("6")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("Class, Method")]
    public async Task Validate_InvalidKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDocumentOutlineParams
            {
                SourceFile = workspace.SourcePath,
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
        Assert.Contains("Method", ex.Message);
    }

    #endregion

    #region Execute nameFilter

    private const string NameSource = """
        namespace App
        {
            public class ClickHandler
            {
                public ClickHandler() { }
                public void HandleClick() { }
                public void Render() { }
                public int HandledCount { get; set; }

                public class KeyRouter
                {
                    public void HandleKey() { }
                    public void Route() { }
                }
            }

            public class Renderer
            {
                public void Draw() { }
                public const int Handles = 1;
            }

            public enum Mode
            {
                Handled,
                Ignored
            }
        }
        """;

    // Full DFS pre-order: App, ClickHandler, KeyRouter, HandleKey, Route, ClickHandler (ctor), HandleClick,
    // Render, HandledCount, Renderer, Draw, Handles, Mode, Handled, Ignored => 15 nodes.
    private const int NameSourceTotal = 15;

    // nameFilter "handle" tree: App > [ClickHandler > [KeyRouter > [HandleKey], ClickHandler (ctor), HandleClick,
    // HandledCount], Renderer > [Handles], Mode > [Handled]] => 11 nodes.
    private const int HandleFilteredTotal = 11;

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new GetDocumentOutlineParams { SourceFile = "/tmp/x.cs" };
        Assert.Null(@params.NameFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task GetDocumentOutline_OmittedOrBlankNameFilter_MatchesTodayOutputExactly(string? nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var baseline = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var filtered = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter
        });

        Assert.True(baseline.Success);
        Assert.NotNull(baseline.Data);
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.Equal(NameSourceTotal, filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal(SerializeOutline(baseline.Data), SerializeOutline(filtered.Data));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterNestedMember_KeepsContainingTypesAsContainers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath
        });
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "HandleKey"
        });

        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);

        // App, ClickHandler and KeyRouter do not contain "HandleKey" and are kept only as containers;
        // their other members (ctor, HandleClick, Route, ...) and the Renderer / Mode subtrees are dropped.
        var flat = Flatten(result.Data.Entries);
        Assert.Equal(
            new[]
            {
                ("App", "Namespace", 1),
                ("ClickHandler", "Class", 2),
                ("KeyRouter", "Class", 3),
                ("HandleKey", "Method", 4)
            },
            flat.Select(e => (e.Name, e.Kind, e.Depth)));

        // Kept entries (containers included) keep their original positions and metadata.
        var original = Flatten(full.Data.Entries);
        Assert.All(flat, e => Assert.Contains(e, original));

        Assert.Equal(4, result.Data.TotalCount);
        Assert.Equal(result.Data.TotalCount, CountNodes(result.Data.Entries));
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterSubstring_KeepsEveryMatchAndItsContainers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "handle"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[]
            {
                ("App", "Namespace", 1),
                ("ClickHandler", "Class", 2),
                ("KeyRouter", "Class", 3),
                ("HandleKey", "Method", 4),
                ("ClickHandler", "Constructor", 3),
                ("HandleClick", "Method", 3),
                ("HandledCount", "Property", 3),
                ("Renderer", "Class", 2),
                ("Handles", "Constant", 3),
                ("Mode", "Enum", 2),
                ("Handled", "EnumMember", 3)
            },
            Flatten(result.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));
        Assert.Equal(HandleFilteredTotal, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterMatchingContainer_DropsItsNonMatchingChildren()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "Router"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(
            new[]
            {
                ("App", "Namespace", 1),
                ("ClickHandler", "Class", 2),
                ("KeyRouter", "Class", 3)
            },
            Flatten(result.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));

        var root = Assert.Single(result.Data.Entries);
        var clickHandler = Assert.Single(root.Children!);
        var keyRouter = Assert.Single(clickHandler.Children!);
        Assert.Null(keyRouter.Children);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterNoMatch_ReturnsEmptyOutline()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "Zzz"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data.Entries);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("handlekey")]
    [InlineData("HANDLEKEY")]
    [InlineData("hAnDlEkEy")]
    [InlineData("  HandleKey  ")]
    public async Task GetDocumentOutline_NameFilter_IsCaseInsensitiveAndIgnoresSurroundingWhitespace(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var canonical = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "HandleKey"
        });
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter
        });

        Assert.True(canonical.Success);
        Assert.NotNull(canonical.Data);
        Assert.Equal(4, canonical.Data.TotalCount);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(SerializeOutline(canonical.Data), SerializeOutline(result.Data));
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterWithKindFilter_RequiresBothToMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // "handle" + Method: the constructor counts as Method; ClickHandler (Class), HandledCount (Property),
        // Handles (Constant) and Handled (EnumMember) match the name but not the kind.
        var methods = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "handle",
            KindFilter = "Method"
        });

        Assert.True(methods.Success);
        Assert.NotNull(methods.Data);
        Assert.Equal(
            new[]
            {
                ("App", "Namespace", 1),
                ("ClickHandler", "Class", 2),
                ("KeyRouter", "Class", 3),
                ("HandleKey", "Method", 4),
                ("ClickHandler", "Constructor", 3),
                ("HandleClick", "Method", 3)
            },
            Flatten(methods.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));
        Assert.Equal(6, methods.Data.TotalCount);
        Assert.False(methods.Data.Truncated);

        // "Render" + Method keeps the Render method only; the Renderer class matches the name but not the kind.
        var render = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "Render",
            KindFilter = "Method"
        });

        Assert.True(render.Success);
        Assert.NotNull(render.Data);
        Assert.Equal(
            new[]
            {
                ("App", "Namespace", 1),
                ("ClickHandler", "Class", 2),
                ("Render", "Method", 3)
            },
            Flatten(render.Data.Entries).Select(e => (e.Name, e.Kind, e.Depth)));
        Assert.Equal(3, render.Data.TotalCount);

        // A name match of the wrong kind with no matching descendant yields an empty outline.
        var none = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "Handles",
            KindFilter = "Method"
        });

        Assert.True(none.Success);
        Assert.NotNull(none.Data);
        Assert.Empty(none.Data.Entries);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterWithMaxDepth_AppliesFilterBeforeDepthCap()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // "HandleKey" tree: App > ClickHandler > KeyRouter > HandleKey (4 nodes); depth 2 keeps App, ClickHandler.
        // Renderer and Mode were removed by the filter, not the depth cap.
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "HandleKey",
            MaxDepth = 2
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "App", "ClickHandler" }, Flatten(result.Data.Entries).Select(e => e.Name));
        var root = Assert.Single(result.Data.Entries);
        Assert.Null(Assert.Single(root.Children!).Children);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterWithMaxResults_AppliesFilterBeforeNodeBudget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "handle",
            MaxResults = 5
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(
            new[] { "App", "ClickHandler", "KeyRouter", "HandleKey", "ClickHandler" },
            Flatten(capped.Data.Entries).Select(e => e.Name));
        Assert.Equal(HandleFilteredTotal, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);

        // maxResults at or above the filtered node count: nothing beyond the filter is dropped.
        var notCapped = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "handle",
            MaxResults = HandleFilteredTotal
        });

        Assert.True(notCapped.Success);
        Assert.NotNull(notCapped.Data);
        Assert.Equal(HandleFilteredTotal, CountNodes(notCapped.Data.Entries));
        Assert.Equal(HandleFilteredTotal, notCapped.Data.TotalCount);
        Assert.False(notCapped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDocumentOutline_NameFilterWithMaxDepthAndMaxResults_AppliesFilterThenDepthThenBudget()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDocumentOutlineOperation(workspace.Context);

        // filter ("handle") -> depth 2 => App, ClickHandler, Renderer, Mode -> budget 3 => App, ClickHandler, Renderer.
        var result = await operation.ExecuteAsync(new GetDocumentOutlineParams
        {
            SourceFile = workspace.SourcePath,
            NameFilter = "handle",
            MaxDepth = 2,
            MaxResults = 3
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(new[] { "App", "ClickHandler", "Renderer" }, Flatten(result.Data.Entries).Select(e => e.Name));
        Assert.Equal(HandleFilteredTotal, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    #endregion

    private static string SerializeOutline(GetDocumentOutlineResult data) =>
        JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static int MaxTreeDepth(IReadOnlyList<OutlineEntry>? entries)
    {
        if (entries is not { Count: > 0 })
            return 0;

        return 1 + entries.Max(e => MaxTreeDepth(e.Children));
    }

    private static List<(string Name, string Kind, int Line, int Column, string? Accessibility, string? ReturnType, int Depth)> Flatten(
        IReadOnlyList<OutlineEntry> entries, int depth = 1)
    {
        var list = new List<(string, string, int, int, string?, string?, int)>();
        foreach (var e in entries)
        {
            list.Add((e.Name, e.Kind, e.Line, e.Column, e.Accessibility, e.ReturnType, depth));
            if (e.Children is { Count: > 0 })
                list.AddRange(Flatten(e.Children, depth + 1));
        }

        return list;
    }

    private static int CountNodes(IReadOnlyList<OutlineEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            count++;
            if (entry.Children is { Count: > 0 })
                count += CountNodes(entry.Children);
        }

        return count;
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static async Task<TempWorkspace> CreateAsync(string source, string fileName = "Foo.cs")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDocumentOutline_" + Guid.NewGuid().ToString("N"));
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

            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, source);

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(path) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {path}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    SourcePath = path,
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
                    // ignore cleanup failures
                }
            });
        }
    }
}

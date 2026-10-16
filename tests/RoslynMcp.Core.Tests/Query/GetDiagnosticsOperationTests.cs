using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Query.Utilities;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetDiagnosticsOperation"/> optional <c>maxResults</c>, <c>diagnosticIds</c>, <c>excludeDiagnosticIds</c>, <c>projectPath</c>, <c>namespaceFilter</c>, <c>accessibilityFilter</c>, <c>messageFilter</c>, <c>kindFilter</c>, and <c>nameFilter</c>
/// (peer of <see cref="FindReferencesOperation"/> / <see cref="SearchSymbolsOperation"/>).
/// </summary>
public class GetDiagnosticsOperationTests
{
    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.MaxResults);
    }

    [SkippableFact]
    public async Task Validate_MaxResultsZero_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { MaxResults = 0 }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Execute truncation

    [SkippableFact]
    public async Task GetDiagnostics_OmittedMaxResults_ReturnsFullListNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.True(result.Data.TotalCount >= 3, $"Expected >= 3 diagnostics, got {result.Data.TotalCount}");
        Assert.Equal(result.Data.TotalCount, result.Data.Diagnostics.Count);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsTruncates_SetsTruncatedTrue()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            #warning Three
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 3);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        // Deterministic: first N of today's enumeration order
        Assert.Equal(full.Data.Diagnostics[0].Id, capped.Data.Diagnostics[0].Id);
        Assert.Equal(full.Data.Diagnostics[0].Message, capped.Data.Diagnostics[0].Message);
        Assert.Equal(full.Data.Diagnostics[1].Id, capped.Data.Diagnostics[1].Id);
        Assert.Equal(full.Data.Diagnostics[1].Message, capped.Data.Diagnostics[1].Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MaxResultsAtOrAboveTotal_NotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning One
            #warning Two
            class C {}
            """);

        var operation = new GetDiagnosticsOperation(workspace.Context);
        var full = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.True(full.Data.TotalCount >= 2);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MaxResults = full.Data.TotalCount
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(full.Data.TotalCount, capped.Data.Diagnostics.Count);
        Assert.False(capped.Data.Truncated);
    }

    #endregion

    #region diagnosticIds filter

    [Fact]
    public void DiagnosticIds_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.DiagnosticIds);
    }

    [SkippableFact]
    public async Task Validate_DiagnosticIdsWhitespaceEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030", " " } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("diagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_EmptyDiagnosticIds_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var empty = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = Array.Empty<string>()
        });

        Assert.True(omitted.Success);
        Assert.True(empty.Success);
        Assert.Equal(omitted.Data!.TotalCount, empty.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            empty.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_KeepsOnlyMatchingIds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS0168" }
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotEmpty(filtered.Data.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS0168", d.Id));
        Assert.Equal(full.Data.Diagnostics.Count(d => d.Id == "CS0168"), filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_MultipleIdsCaseInsensitive()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Id is "CS1030" or "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "cs1030", " Cs0168 " }
        });

        Assert.True(filtered.Success);
        Assert.Equal(expected, filtered.Data!.TotalCount);
        Assert.Equal(expected, filtered.Data.Diagnostics.Count);
        Assert.All(filtered.Data.Diagnostics, d => Assert.True(d.Id is "CS1030" or "CS0168", d.Id));
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIds_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS9999" }
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_DiagnosticIdsWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" }
        });
        Assert.True(filtered.Success);
        Assert.True(filtered.Data!.TotalCount >= 3, $"Expected >= 3 CS1030, got {filtered.Data.TotalCount}");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(filtered.Data.TotalCount, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Diagnostics, d => Assert.Equal("CS1030", d.Id));
    }

    private const string MixedSource = """
        #warning One
        #warning Two
        #warning Three
        class C
        {
            void M()
            {
                int unused;
            }
        }
        """;

    #endregion

    #region messageFilter

    // Filters use identifiers / #warning text that appear verbatim in the message in every locale
    // ('unusedLocal', 'Two'), so the assertions do not depend on the compiler's message language.

    [Fact]
    public void MessageFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.MessageFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task GetDiagnostics_MessageFilterBlank_ReturnsSameAsOmitted(string messageFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var blank = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = messageFilter
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(omitted.Data!.TotalCount, blank.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            blank.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilter_KeepsOnlyMessagesContainingValue()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168" && d.Message.Contains("unusedLocal", StringComparison.Ordinal));

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = "unusedLocal"
        });

        Assert.True(filtered.Success);
        var diagnostic = Assert.Single(filtered.Data!.Diagnostics);
        Assert.Equal("CS0168", diagnostic.Id);
        Assert.Contains("unusedLocal", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilter_CaseInsensitiveAndTrimmed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var exact = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = "Two"
        });
        var folded = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = "  tWO \t"
        });

        Assert.True(exact.Success);
        Assert.True(folded.Success);
        var diagnostic = Assert.Single(exact.Data!.Diagnostics);
        Assert.Equal("CS1030", diagnostic.Id);
        Assert.Equal(
            exact.Data.Diagnostics.Select(d => d.Id + d.Message),
            folded.Data!.Diagnostics.Select(d => d.Id + d.Message));
        Assert.Equal(1, folded.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilter_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = "NoDiagnosticMentionsThis"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilterWithDiagnosticIds_BothMustMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var disjoint = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            MessageFilter = "unusedLocal"
        });
        Assert.True(disjoint.Success);
        Assert.Empty(disjoint.Data!.Diagnostics);
        Assert.Equal(0, disjoint.Data.TotalCount);

        var overlap = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS0168" },
            MessageFilter = "unused"
        });
        Assert.True(overlap.Success);
        Assert.Equal(2, overlap.Data!.TotalCount);
        Assert.All(overlap.Data.Diagnostics, d =>
        {
            Assert.Equal("CS0168", d.Id);
            Assert.Contains("unused", d.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilterWithAccessibilityFilter_BothMustMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            AccessibilityFilter = "public",
            MessageFilter = "unused"
        });

        Assert.True(result.Success);
        var diagnostic = Assert.Single(result.Data!.Diagnostics);
        Assert.Equal("CS0168", diagnostic.Id);
        Assert.Contains("unusedOther", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_MessageFilterWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MessageSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Message.Contains("unused", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, expected);
        Assert.True(full.Data.TotalCount > expected, "Fixture should also report diagnostics the filter drops.");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            MessageFilter = "UNUSED",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.Equal(expected, capped.Data!.TotalCount);
        var diagnostic = Assert.Single(capped.Data.Diagnostics);
        Assert.Contains("unused", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(capped.Data.Truncated);
    }

    private const string MessageSource = """
        #warning One
        #warning Two
        #warning Three
        class C
        {
            void M()
            {
                int unusedLocal;
            }

            public void N()
            {
                int unusedOther;
            }
        }
        """;

    #endregion

    #region kindFilter

    // Site-kind KindFilter: unused field → Field, unused local in a method → Method, unused local in a
    // property getter → Method (accessor), unused using → namespace-level (never matches a named kind,
    // including Namespace). Variable names identify sites. (Unused const fields do not reliably emit
    // CS0414 in this fixture, so Constant is covered only via SymbolKindMapper elsewhere.)

    private const string KindSource = """
        using System.Text;

        namespace App
        {
            using System.Collections.Generic;

            public class Host
            {
                private int unusedField;
                void M() { int unusedInMethod; }
                int Prop
                {
                    get { int unusedInGetter; return 0; }
                }
            }
        }
        """;

    private static readonly string[] KindDiagnosticIds = { "CS0168", "CS0169", "CS8019" };

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task GetDiagnostics_KindFilterBlank_ReturnsSameAsOmitted(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath
        });
        var blank = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(omitted.Data!.TotalCount, blank.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            blank.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("Method", new[] { "unusedInGetter", "unusedInMethod" })]
    [InlineData("Field", new[] { "Host.unusedField" })]
    public async Task GetDiagnostics_KindFilter_SelectsSitesByEnclosingSymbolKind(
        string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });

        Assert.True(result.Success, kindFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("METHOD", "Method")]
    [InlineData("  Field  ", "Field")]
    public async Task GetDiagnostics_KindFilter_IsCaseInsensitiveAndTrimmed(string kindFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });
        var expected = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.Diagnostics);
        Assert.Equal(
            VariableNames(expected.Data).OrderBy(n => n, StringComparer.Ordinal),
            VariableNames(filtered.Data!).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("Widget")]
    [InlineData("6")]
    [InlineData("Class, Method")]
    public async Task GetDiagnostics_UnknownKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams
            {
                SeverityFilter = "All",
                DiagnosticIds = KindDiagnosticIds,
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.StartsWith($"Invalid kindFilter '{kindFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilter_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Property"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilter_NamespaceLevelSitesNeverMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS8019");

        foreach (var kind in new[] { "Method", "Field", "Namespace", "Class" })
        {
            var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
            {
                SeverityFilter = "All",
                DiagnosticIds = KindDiagnosticIds,
                SourceFile = workspace.SourcePath,
                KindFilter = kind
            });
            Assert.True(filtered.Success, kind);
            Assert.DoesNotContain(filtered.Data!.Diagnostics, d => d.Id == "CS8019");
        }
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int unusedInMethod; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            KindFilter = "Method"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilterWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Method"
        });
        Assert.True(filtered.Success);
        Assert.Equal(2, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.TotalCount);
        Assert.Single(capped.Data.Diagnostics);
        Assert.True(capped.Data.Truncated);
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilterWithAccessibilityFilter_BothMustMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        // unusedField is private Field; unusedInMethod is private Method (implicit).
        var fieldPrivate = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Field",
            AccessibilityFilter = "private"
        });
        Assert.True(fieldPrivate.Success);
        Assert.Equal(new[] { "Host.unusedField" }, VariableNames(fieldPrivate.Data!));

        var fieldPublic = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Field",
            AccessibilityFilter = "public"
        });
        Assert.True(fieldPublic.Success);
        Assert.Empty(fieldPublic.Data!.Diagnostics);
        Assert.Equal(0, fieldPublic.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_KindFilterWithMessageFilter_BothMustMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(KindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = KindDiagnosticIds,
            SourceFile = workspace.SourcePath,
            KindFilter = "Method",
            MessageFilter = "unusedInGetter"
        });

        Assert.True(result.Success);
        var diagnostic = Assert.Single(result.Data!.Diagnostics);
        Assert.Equal("CS0168", diagnostic.Id);
        Assert.Contains("unusedInGetter", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    // Expression-bodied property / indexer bodies are getter bodies: a diagnostic inside
    // `int P => Missing;` is an accessor (Method) site exactly like the same diagnostic inside a
    // block-bodied getter (`get { return Missing; }`) or an arrow-bodied accessor (`get => Missing;`),
    // while diagnostics on the property declaration itself (its type, or its initializer) stay
    // Property sites. Names in the CS0103 / CS0246 messages identify sites.
    private const string ExpressionBodiedKindSource = """
        namespace App
        {
            public class Host
            {
                public int ExprProp => missingInExprProp;
                public int BlockProp { get { return missingInBlockGetter; } }
                public int ArrowAccessorProp { get => missingInArrowGetter; }
                public int this[int i] => missingInExprIndexer;
                public System.Func<int> LambdaProp => () => missingInExprLambda;
                private int PrivateExprProp => missingInPrivateExprProp;
                public MissingPropType TypedProp { get; set; }
                public int InitProp { get; } = missingInInitializer;
            }
        }
        """;

    private static readonly string[] ExpressionBodiedKindIds = { "CS0103", "CS0246" };

    [SkippableTheory]
    [InlineData("Method", new[]
    {
        "missingInArrowGetter", "missingInBlockGetter", "missingInExprIndexer", "missingInExprLambda",
        "missingInExprProp", "missingInPrivateExprProp"
    })]
    [InlineData("Property", new[] { "MissingPropType", "missingInInitializer" })]
    public async Task GetDiagnostics_KindFilter_ExpressionBodiedPropertyBodyIsAccessorSite(
        string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExpressionBodiedKindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ExpressionBodiedKindIds,
            SourceFile = workspace.SourcePath,
            KindFilter = kindFilter
        });

        Assert.True(result.Success, kindFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ExpressionBodiedPropertyBodyKeepsPropertyAccessibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(ExpressionBodiedKindSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        // The getter of an expression-bodied property has the property's accessibility, so judging the
        // body as the getter does not change accessibilityFilter results.
        var privateSites = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ExpressionBodiedKindIds,
            SourceFile = workspace.SourcePath,
            AccessibilityFilter = "private"
        });
        Assert.True(privateSites.Success);
        Assert.Equal(new[] { "missingInPrivateExprProp" }, VariableNames(privateSites.Data!).ToArray());

        var publicSites = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ExpressionBodiedKindIds,
            SourceFile = workspace.SourcePath,
            AccessibilityFilter = "public"
        });
        Assert.True(publicSites.Success);
        Assert.Equal(
            new[]
            {
                "MissingPropType", "missingInArrowGetter", "missingInBlockGetter", "missingInExprIndexer",
                "missingInExprLambda", "missingInExprProp", "missingInInitializer"
            },
            VariableNames(publicSites.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    #endregion

    #region nameFilter

    // Site-name NameFilter: judged on the simple ISymbol.Name of the same declaring site symbol that
    // accessibilityFilter / kindFilter judge. Unused field → the field's name; unused locals → the
    // declaring method (lambdas looked through), constructor (.ctor), or accessor (get_X); unused usings
    // are namespace-level sites and never match. Variable names in the messages identify sites.
    private const string NameSource = """
        using System.Text;

        namespace App
        {
            using System.Collections.Generic;

            public class OrderService
            {
                private int unusedField;
                public void HandleOrder() { int unusedInHandleOrder; }
                public void OrderHandler() { System.Action a = () => { int unusedInLambda; }; }
                private void ReorderItems() { int unusedInReorder; }
                void Process() { int unusedInProcess; }
                public OrderService() { int unusedInCtor; }
                int Total { get { int unusedInGetter; return 0; } }
            }
        }
        """;

    private static readonly string[] NameDiagnosticIds = { "CS0168", "CS0169", "CS8019" };

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.NameFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task GetDiagnostics_NameFilterBlank_ReturnsSameAsOmitted(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath
        });
        var blank = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter
        });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS8019");
        Assert.Equal(omitted.Data.TotalCount, blank.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            blank.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("Handle", new[] { "unusedInHandleOrder", "unusedInLambda" })]
    [InlineData("handle", new[] { "unusedInHandleOrder", "unusedInLambda" })]
    [InlineData("  HANDLE  ", new[] { "unusedInHandleOrder", "unusedInLambda" })]
    [InlineData("Order", new[] { "unusedInHandleOrder", "unusedInLambda", "unusedInReorder" })]
    [InlineData("Process", new[] { "unusedInProcess" })]
    [InlineData("unusedField", new[] { "OrderService.unusedField" })]
    [InlineData(".ctor", new[] { "unusedInCtor" })]
    [InlineData("get_Total", new[] { "unusedInGetter" })]
    public async Task GetDiagnostics_NameFilter_SelectsSitesBySiteSymbolName(string nameFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter
        });

        Assert.True(result.Success, nameFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NameFilter_NoMatch_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = "Invoice"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App")]
    [InlineData("System")]
    [InlineData("Generic")]
    public async Task GetDiagnostics_NameFilter_NamespaceLevelSitesNeverMatch(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter
        });

        Assert.True(result.Success, nameFilter);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NameFilter_TopLevelStatementsNeverMatchSynthesizedEntryPoint()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "int unusedTopLevel;\nnamespace App { public class Program2 { public static void Main2() { int unusedInMain2; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS0168" },
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Contains("unusedTopLevel", VariableNames(omitted.Data!));

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS0168" },
            SourceFile = workspace.SourcePath,
            NameFilter = "Main"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "unusedInMain2" }, VariableNames(result.Data!).ToArray());
    }

    [SkippableFact]
    public async Task GetDiagnostics_NameFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int unusedInMethod; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            NameFilter = "M"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_NameFilterWithMaxResults_CapsAfterFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = "Handle"
        });
        Assert.True(filtered.Success);
        Assert.Equal(2, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = "Handle",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.TotalCount);
        Assert.Single(capped.Data.Diagnostics);
        Assert.True(capped.Data.Truncated);
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
    }

    [SkippableTheory]
    [InlineData("private", new[] { "unusedInReorder" })]
    [InlineData("public", new[] { "unusedInHandleOrder", "unusedInLambda" })]
    [InlineData("internal", new string[0])]
    public async Task GetDiagnostics_NameFilterWithAccessibilityFilter_BothMustMatch(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = "Order",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success, accessibilityFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
    }

    [SkippableTheory]
    [InlineData("Order", "Method", new[] { "unusedInHandleOrder", "unusedInLambda", "unusedInReorder" })]
    [InlineData("Order", "Class", new string[0])]
    [InlineData("unused", "Field", new[] { "OrderService.unusedField" })]
    [InlineData("unused", "Method", new string[0])]
    public async Task GetDiagnostics_NameFilterWithKindFilter_BothMustMatch(
        string nameFilter, string kindFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = nameFilter,
            KindFilter = kindFilter
        });

        Assert.True(result.Success, $"{nameFilter}/{kindFilter}");
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NameFilterWithMessageFilter_BothMustMatch()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NameSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = NameDiagnosticIds,
            SourceFile = workspace.SourcePath,
            NameFilter = "Handle",
            MessageFilter = "unusedInLambda"
        });

        Assert.True(result.Success);
        var diagnostic = Assert.Single(result.Data!.Diagnostics);
        Assert.Equal("CS0168", diagnostic.Id);
        Assert.Contains("unusedInLambda", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    #endregion


    #region excludeDiagnosticIds filter

    [Fact]
    public void ExcludeDiagnosticIds_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.ExcludeDiagnosticIds);
    }

    [SkippableFact]
    public async Task Validate_ExcludeDiagnosticIdsWhitespaceEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ExcludeDiagnosticIds = new[] { "CS1030", " " } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("excludeDiagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ExcludeDiagnosticIdsEmptyStringEntry_ThrowsMissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ExcludeDiagnosticIds = new[] { string.Empty } }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("excludeDiagnosticIds entries must be non-empty.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_EmptyExcludeDiagnosticIds_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var empty = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = Array.Empty<string>()
        });

        Assert.True(omitted.Success);
        Assert.True(empty.Success);
        Assert.Equal(omitted.Data!.TotalCount, empty.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            empty.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_DropsMatchingId()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        Assert.Contains(full.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Contains(full.Data.Diagnostics, d => d.Id == "CS0168");

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" }
        });

        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS0168");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS1030");
        Assert.Equal(full.Data.Diagnostics.Count(d => d.Id != "CS0168"), filtered.Data.TotalCount);
        Assert.Equal(filtered.Data.TotalCount, filtered.Data.Diagnostics.Count);
        Assert.False(filtered.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_MultipleIdsCaseInsensitiveWithPadding()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var expected = full.Data!.Diagnostics.Count(d => d.Id is not ("CS1030" or "CS0168"));

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "cs1030", " Cs0168 " }
        });

        Assert.True(filtered.Success);
        Assert.Equal(expected, filtered.Data!.TotalCount);
        Assert.Equal(expected, filtered.Data.Diagnostics.Count);
        Assert.All(filtered.Data.Diagnostics, d => Assert.False(d.Id is "CS1030" or "CS0168", d.Id));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_AllIds_ReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(full.Success);
        var allIds = full.Data!.Diagnostics.Select(d => d.Id).Distinct().ToArray();
        Assert.NotEmpty(allIds);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = allIds.Select(id => id.ToLowerInvariant()).ToArray()
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIds_NoMatch_ReturnsSameAsOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS9999" }
        });

        Assert.True(omitted.Success);
        Assert.True(result.Success);
        Assert.Equal(omitted.Data!.TotalCount, result.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            result.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIdsWithDiagnosticIds_ExclusionWins()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var included = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030", "CS0168" }
        });
        Assert.True(included.Success);
        var expected = included.Data!.Diagnostics.Count(d => d.Id == "CS1030");
        Assert.True(expected >= 3, $"Expected >= 3 CS1030, got {expected}");

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030", "CS0168" },
            ExcludeDiagnosticIds = new[] { "cs0168" }
        });

        Assert.True(result.Success);
        Assert.Equal(expected, result.Data!.TotalCount);
        Assert.All(result.Data.Diagnostics, d => Assert.Equal("CS1030", d.Id));

        var fullOverlap = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            DiagnosticIds = new[] { "CS1030" },
            ExcludeDiagnosticIds = new[] { "CS1030" }
        });

        Assert.True(fullOverlap.Success);
        Assert.Empty(fullOverlap.Data!.Diagnostics);
        Assert.Equal(0, fullOverlap.Data.TotalCount);
        Assert.False(fullOverlap.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ExcludeDiagnosticIdsWithMaxResults_CapsAfterExclusion()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" }
        });
        Assert.True(filtered.Success);
        Assert.True(filtered.Data!.TotalCount >= 3, $"Expected >= 3 after exclusion, got {filtered.Data.TotalCount}");

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ExcludeDiagnosticIds = new[] { "CS0168" },
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(filtered.Data.TotalCount, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.DoesNotContain(capped.Data.Diagnostics, d => d.Id == "CS0168");
        Assert.Equal(
            filtered.Data.Diagnostics.Take(2).Select(d => d.Id + d.Message),
            capped.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    #endregion

    #region projectPath filter

    [Fact]
    public void ProjectPath_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.ProjectPath);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathRelative_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = Path.Combine("Lib", "Lib.csproj") }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableTheory]
    [InlineData("C:Lib.csproj")]
    [InlineData("C:Lib\\Lib.csproj")]
    [InlineData("\\Lib.csproj")]
    [InlineData("\\Lib\\Lib.csproj")]
    public async Task Validate_ProjectPathDriveOrRootRelative_ThrowsInvalidSourcePath(string projectPath)
    {
        // Windows drive-relative / root-relative forms pass Path.IsPathRooted but are not fully
        // qualified; on non-Windows they are plain relative paths. Both must be InvalidSourcePath,
        // never resolved against process state into a project match or SourceNotInWorkspace.
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = projectPath }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be an absolute path.", ex.Message);
    }

    [SkippableFact]
    public async Task Validate_ProjectPathNotCsproj_ThrowsInvalidSourcePath()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = workspace.SourcePath }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
        Assert.Equal("projectPath must be a .csproj file.", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathNotInWorkspace_ThrowsSourceNotInWorkspace()
    {
        await using var workspace = await TempWorkspace.CreateAsync("#warning One\nclass C {}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var missing = Path.Combine(workspace.DirectoryPath, "Other", "Other.csproj");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams { ProjectPath = missing }));

        Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        Assert.Equal($"Project not found in workspace: {missing}", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathBlank_MatchesOmitted()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" } });
        var blank = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" }, ProjectPath = "  " });

        Assert.True(omitted.Success);
        Assert.True(blank.Success);
        Assert.Equal(
            omitted.Data!.Diagnostics.Select(d => d.File + d.Message),
            blank.Data!.Diagnostics.Select(d => d.File + d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathOmitted_ReturnsEveryProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = new[] { "CS1030" } });

        Assert.True(result.Success);
        Assert.Contains(result.Data!.Diagnostics, d => d.Message.Contains("LibWarning", StringComparison.Ordinal));
        Assert.Contains(result.Data.Diagnostics, d => d.Message.Contains("AppWarning", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPath_RestrictsToThatProject()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var lib = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath
        });

        Assert.True(lib.Success);
        Assert.NotEmpty(lib.Data!.Diagnostics);
        Assert.Equal(lib.Data.Diagnostics.Count, lib.Data.TotalCount);
        Assert.All(lib.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        Assert.All(lib.Data.Diagnostics, d => Assert.Contains("LibWarning", d.Message, StringComparison.Ordinal));

        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.AppProjectPath
        });

        Assert.True(app.Success);
        Assert.NotEmpty(app.Data!.Diagnostics);
        Assert.All(app.Data.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));
        Assert.All(app.Data.Diagnostics, d => Assert.Contains("AppWarning", d.Message, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathDifferentCasing_MatchesOnlyOnCaseInsensitiveVolume()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var wrongCased = Path.Combine(Path.GetDirectoryName(workspace.LibProjectPath)!, "LIB.CSPROJ");
        var caseInsensitiveVolume = File.Exists(wrongCased);

        var @params = new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = wrongCased
        };

        if (caseInsensitiveVolume)
        {
            // Windows / default macOS: the wrong-cased alias is the same physical project.
            var result = await operation.ExecuteAsync(@params);
            Assert.True(result.Success);
            Assert.NotEmpty(result.Data!.Diagnostics);
            Assert.All(result.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        }
        else
        {
            // Case-sensitive volume (Linux): LIB.CSPROJ is a different, nonexistent project.
            var ex = await Assert.ThrowsAsync<RefactoringException>(() => operation.ExecuteAsync(@params));
            Assert.Equal(ErrorCodes.SourceNotInWorkspace, ex.ErrorCode);
        }
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathWithMaxResults_CapsAfterProjectFilter()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            libSource: "#warning LibWarning1\n#warning LibWarning2\n#warning LibWarning3\nnamespace Lib { public class Animal {} }\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(filtered.Success);
        Assert.Equal(3, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.LibProjectPath,
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        Assert.All(capped.Data.Diagnostics, d => Assert.Equal(workspace.LibSourcePath, d.File));
        Assert.Equal(
            filtered.Data.Diagnostics.Take(2).Select(d => d.Message),
            capped.Data.Diagnostics.Select(d => d.Message));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathWithSourceFileFromOtherProject_ReturnsEmpty()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync();
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            ProjectPath = workspace.LibProjectPath,
            SourceFile = workspace.AppSourcePath,
            SeverityFilter = "All"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);

        var sameProject = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS1030" },
            ProjectPath = workspace.AppProjectPath,
            SourceFile = workspace.AppSourcePath
        });

        Assert.True(sameProject.Success);
        Assert.NotEmpty(sameProject.Data!.Diagnostics);
        Assert.All(sameProject.Data.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));
    }

    [SkippableFact]
    public async Task GetDiagnostics_ProjectPathSingleProject_MatchesOmitted()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            ProjectPath = workspace.ProjectPath
        });

        Assert.True(omitted.Success);
        Assert.True(scoped.Success);
        Assert.True(scoped.Data!.TotalCount >= 3);
        Assert.Equal(omitted.Data!.TotalCount, scoped.Data.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message),
            scoped.Data.Diagnostics.Select(d => d.Id + d.Message));
    }

    #endregion

    #region namespaceFilter

    // One CS0168 ("declared but never used") per namespace; the variable name identifies the site.
    private const string NamespacedSource = """
        namespace App.Services
        {
            public class S { public void M() { int inServices; } }
        }

        namespace App.Services.Orders
        {
            public class O { public void M() { int inOrders; } }
        }

        namespace App.ServicesExtra
        {
            public class E { public void M() { int inExtra; } }
        }

        namespace Other
        {
            public class X { public void M() { int inOther; } }
        }

        public class G { public void M() { int inGlobal; } }
        """;

    private static readonly string[] Cs0168 = { "CS0168" };

    private static IEnumerable<string> VariableNames(GetDiagnosticsResult result) =>
        result.Diagnostics.Select(d =>
        {
            var start = d.Message.IndexOf('\'');
            var end = d.Message.IndexOf('\'', start + 1);
            return d.Message[(start + 1)..end];
        });

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public async Task GetDiagnostics_NamespaceFilterBlankOrBareGlobal_MatchesOmitted(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = Cs0168 });
        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = namespaceFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(5, omitted.Data!.TotalCount);
        Assert.Equal(omitted.Data.TotalCount, filtered.Data!.TotalCount);
        // Method-body diagnostics are bound concurrently, so compare as sets.
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            filtered.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_KeepsEqualAndNestedSites()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inOrders", "inServices" }, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(2, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.All(result.Data.Diagnostics, d => Assert.Equal(workspace.SourcePath, d.File));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterNested_ExcludesParentNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services.Orders"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inOrders" }, VariableNames(result.Data!));
        Assert.Equal(1, result.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterOtherNamespace_DropsPrefixLookalikeAndGlobal()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var other = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Other"
        });
        Assert.True(other.Success);
        Assert.Equal(new[] { "inOther" }, VariableNames(other.Data!));

        // App matches App.Services, App.Services.Orders, and App.ServicesExtra but never the global G.
        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(app.Success);
        Assert.Equal(
            new[] { "inExtra", "inOrders", "inServices" },
            VariableNames(app.Data!).OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("inGlobal", VariableNames(app.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterTrimmedWithGlobalPrefix_MatchesPlainFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var plain = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        var prefixed = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "  global::App.Services  "
        });

        Assert.True(plain.Success);
        Assert.True(prefixed.Success);
        Assert.Equal(2, prefixed.Data!.TotalCount);
        Assert.Equal(
            plain.Data!.Diagnostics.Select(d => d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            prefixed.Data.Diagnostics.Select(d => d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterIsOrdinal_DifferentCaseReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "app.services"
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            NamespaceFilter = "Nowhere.At.All",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_FileScopedNamespace()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App.Services.Orders;

            public class O { public void M() { int inFileScoped; } }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var kept = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        Assert.True(kept.Success);
        Assert.Equal(new[] { "inFileScoped" }, VariableNames(kept.Data!));

        var dropped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.ServicesExtra"
        });
        Assert.True(dropped.Success);
        Assert.Empty(dropped.Data!.Diagnostics);
        Assert.Equal(0, dropped.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_NamespaceLevelSiteUsesThatNamespace()
    {
        // A using directive inside a namespace declaration encloses to the namespace itself
        // (same rule as find_references); the one outside any namespace is a global-namespace site.
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text;

            namespace App.Services
            {
                using System.Collections.Generic;

                public class S { }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        // sourceFile scopes away the SDK-generated AssemblyInfo usings (also CS8019).
        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019" },
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.TotalCount);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019" },
            NamespaceFilter = "App.Services"
        });

        Assert.True(filtered.Success);
        var only = Assert.Single(filtered.Data!.Diagnostics);
        Assert.Equal(5, only.Line);
        Assert.Equal(1, filtered.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        // An Exe with no Main reports CS5001 with Location.None; it has no site, so it never
        // matches a named filter (but is still returned when the filter is omitted).
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int inApp; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            NamespaceFilter = "App"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithMaxResults_CapsAfterNamespaceFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedSource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(filtered.Success);
        Assert.Equal(3, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.Equal(3, capped.Data!.TotalCount);
        Assert.Equal(2, capped.Data.Diagnostics.Count);
        Assert.True(capped.Data.Truncated);
        // Every returned diagnostic comes from the namespace-filtered set (method-body diagnostics are
        // bound concurrently, so which two survive the cap is not asserted).
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
        Assert.Equal(2, VariableNames(capped.Data).Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithSourceFile_MustSatisfyBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            NamespacedSource,
            extraFiles: new Dictionary<string, string>
            {
                ["Bar.cs"] = "namespace App.Services { public class B { public void M() { int inBar; } } }\n"
            });
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var both = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services"
        });
        Assert.True(both.Success);
        Assert.Equal(
            new[] { "inBar", "inOrders", "inServices" },
            VariableNames(both.Data!).OrderBy(n => n, StringComparer.Ordinal));

        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App.Services",
            SourceFile = barPath
        });
        Assert.True(scoped.Success);
        Assert.Equal(new[] { "inBar" }, VariableNames(scoped.Data!));
        Assert.Equal(1, scoped.Data!.TotalCount);

        var none = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Other",
            SourceFile = barPath
        });
        Assert.True(none.Success);
        Assert.Empty(none.Data!.Diagnostics);
        Assert.Equal(0, none.Data.TotalCount);
        Assert.False(none.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_NamespaceFilterWithProjectPath_MustSatisfyBoth()
    {
        await using var workspace = await TwoProjectWorkspace.CreateAsync(
            libSource: "namespace Lib { public class Animal { public void M() { int inLib; } } }\n",
            appSource: "namespace App { public class Dog : Lib.Animal { public void N() { int inApp; } } }\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var app = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App"
        });
        Assert.True(app.Success);
        Assert.Equal(new[] { "inApp" }, VariableNames(app.Data!));
        Assert.All(app.Data!.Diagnostics, d => Assert.Equal(workspace.AppSourcePath, d.File));

        var mismatched = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "App",
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(mismatched.Success);
        Assert.Empty(mismatched.Data!.Diagnostics);
        Assert.Equal(0, mismatched.Data.TotalCount);

        var lib = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            NamespaceFilter = "Lib",
            ProjectPath = workspace.LibProjectPath
        });
        Assert.True(lib.Success);
        Assert.Equal(new[] { "inLib" }, VariableNames(lib.Data!));
    }

    #endregion

    #region accessibilityFilter

    // One CS0168 ("declared but never used") per accessibility site; the variable name identifies the site.
    // Lambdas / local functions are looked through to the declaring member (Lam → public, Loc → internal).
    private const string AccessibilitySource = """
        namespace App;
        public class Host
        {
            public void Pub() { int inPublic; }
            internal void Int() { int inInternal; }
            protected void Prot() { int inProtected; }
            private void Priv() { int inPrivate; }
            protected internal void ProtInt() { int inProtInt; }
            private protected void PrivProt() { int inPrivProt; }
            void DefaultPriv() { int inDefaultPrivate; }
            public void Lam() { System.Action a = () => { int inLambda; }; a(); }
            internal void Loc() { void Inner() { int inLocalFunc; } Inner(); }
        }
        """;

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new GetDiagnosticsParams();
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetDiagnostics_BlankAccessibilityFilter_MatchesOmitted(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = Cs0168 });
        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(omitted.Success);
        Assert.True(filtered.Success);
        Assert.Equal(9, omitted.Data!.TotalCount);
        Assert.Equal(omitted.Data.TotalCount, filtered.Data!.TotalCount);
        Assert.Equal(
            omitted.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal),
            filtered.Data.Diagnostics.Select(d => d.Id + d.Message + d.Line).OrderBy(x => x, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("public", new[] { "inLambda", "inPublic" })]
    [InlineData("internal", new[] { "inInternal", "inLocalFunc" })]
    [InlineData("protected", new[] { "inProtected" })]
    [InlineData("private", new[] { "inDefaultPrivate", "inPrivate" })]
    [InlineData("protected internal", new[] { "inProtInt" })]
    [InlineData("private protected", new[] { "inPrivProt" })]
    public async Task GetDiagnostics_AccessibilityFilter_SelectsSitesByEnclosingSymbolDeclaredAccessibility(
        string accessibilityFilter, string[] expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success, accessibilityFilter);
        Assert.Equal(expected, VariableNames(result.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ImplicitPrivateDefault()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.Contains("inDefaultPrivate", VariableNames(result.Data!));
        Assert.DoesNotContain("inPublic", VariableNames(result.Data!));
    }

    [SkippableTheory]
    [InlineData("PUBLIC", "public")]
    [InlineData("  Private  ", "private")]
    [InlineData("Protected   Internal", "protected internal")]
    [InlineData("internal protected", "protected internal")]
    [InlineData("protected private", "private protected")]
    public async Task GetDiagnostics_AccessibilityFilter_IsCaseInsensitiveAndAcceptsEitherModifierOrder(
        string accessibilityFilter, string canonical)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = accessibilityFilter
        });
        var expected = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = canonical
        });

        Assert.True(filtered.Success);
        Assert.True(expected.Success);
        Assert.NotEmpty(expected.Data!.Diagnostics);
        Assert.Equal(
            VariableNames(expected.Data).OrderBy(n => n, StringComparer.Ordinal),
            VariableNames(filtered.Data!).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableTheory]
    [InlineData("friend")]
    [InlineData("ProtectedOrInternal")]
    [InlineData("public, internal")]
    public async Task GetDiagnostics_UnknownAccessibilityFilter_ThrowsInvalidVisibility(string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = Cs0168,
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.StartsWith($"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: ", ex.Message);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ProtectedInternalNotMatchedByProtectedOrInternal()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var protInt = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "protected internal"
        });
        var prot = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "protected"
        });
        var intern = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "internal"
        });

        Assert.True(protInt.Success);
        Assert.Equal(new[] { "inProtInt" }, VariableNames(protInt.Data!));
        Assert.DoesNotContain("inProtInt", VariableNames(prot.Data!));
        Assert.DoesNotContain("inProtInt", VariableNames(intern.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_LambdaAndLocalFunctionJudgedByDeclaringMember()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public"
        });
        var intern = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "internal"
        });

        Assert.True(pub.Success);
        Assert.Contains("inLambda", VariableNames(pub.Data!));
        Assert.DoesNotContain("inLocalFunc", VariableNames(pub.Data!));

        Assert.True(intern.Success);
        Assert.Contains("inLocalFunc", VariableNames(intern.Data!));
        Assert.DoesNotContain("inLambda", VariableNames(intern.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_NamespaceLevelSitesNeverMatch()
    {
        // Roslyn reports INamespaceSymbol.DeclaredAccessibility as Public, so an unused using inside
        // a namespace declaration must still be excluded when filtering for public.
        await using var workspace = await TempWorkspace.CreateAsync("""
            using System.Text;

            namespace App.Services
            {
                using System.Collections.Generic;

                public class S { public void M() { int inPublic; } }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019", "CS0168" },
            SourceFile = workspace.SourcePath
        });
        Assert.True(omitted.Success);
        Assert.Equal(3, omitted.Data!.TotalCount);

        var publicSites = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = new[] { "CS8019", "CS0168" },
            SourceFile = workspace.SourcePath,
            AccessibilityFilter = "public"
        });

        Assert.True(publicSites.Success);
        Assert.Equal(new[] { "inPublic" }, VariableNames(publicSites.Data!));
        Assert.DoesNotContain(publicSites.Data!.Diagnostics, d => d.Id == "CS8019");
        Assert.Equal(1, publicSites.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DropsDiagnosticsWithoutSourceLocation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            "namespace App { public class C { public void M() { int inPublic; } } }\n",
            outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { SeverityFilter = "Warning" });
        Assert.True(omitted.Success);
        Assert.Contains(omitted.Data!.Diagnostics, d => d.Id == "CS5001" && d.File == null);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "Warning",
            AccessibilityFilter = "public"
        });

        Assert.True(filtered.Success);
        Assert.NotEmpty(filtered.Data!.Diagnostics);
        Assert.All(filtered.Data.Diagnostics, d => Assert.NotNull(d.File));
        Assert.DoesNotContain(filtered.Data.Diagnostics, d => d.Id == "CS5001");
        Assert.Contains(filtered.Data.Diagnostics, d => d.Id == "CS0168");
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithMaxResults_CapsAfterAccessibilityFilter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AccessibilitySource);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public"
        });
        Assert.True(filtered.Success);
        Assert.Equal(2, filtered.Data!.TotalCount);

        var capped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.Equal(2, capped.Data!.TotalCount);
        Assert.Single(capped.Data.Diagnostics);
        Assert.True(capped.Data.Truncated);
        Assert.All(VariableNames(capped.Data), n => Assert.Contains(n, VariableNames(filtered.Data)));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterNoMatch_ReturnsEmptySuccess()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class OnlyPub { public void Go() { int inPublic; } }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private",
            MaxResults = 1
        });

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Diagnostics);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithNamespaceFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            namespace App.Services
            {
                public class S
                {
                    public void Pub() { int inServicesPublic; }
                    private void Priv() { int inServicesPrivate; }
                }
            }

            namespace Other
            {
                public class O
                {
                    public void Pub() { int inOtherPublic; }
                }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.Equal(new[] { "inServicesPublic" }, VariableNames(result.Data!));
        Assert.Equal(1, result.Data!.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilterWithSourceFile_MustSatisfyBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            AccessibilitySource,
            extraFiles: new Dictionary<string, string>
            {
                ["Bar.cs"] = "namespace App; public class Bar { public void M() { int inBarPublic; } private void H() { int inBarPrivate; } }\n"
            });
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var barPath = Path.Combine(workspace.DirectoryPath, "Bar.cs");
        var scoped = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "public",
            SourceFile = barPath
        });
        Assert.True(scoped.Success);
        Assert.Equal(new[] { "inBarPublic" }, VariableNames(scoped.Data!));
        Assert.Equal(1, scoped.Data!.TotalCount);

        var none = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = Cs0168,
            AccessibilityFilter = "private",
            SourceFile = barPath
        });
        Assert.True(none.Success);
        Assert.Equal(new[] { "inBarPrivate" }, VariableNames(none.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DeclarationAttachedFieldDiagnosticsUseFieldAccessibility()
    {
        // CS0169 / CS0414 attach to the field identifier. GetEnclosingSymbol is the enclosing public
        // type (a field without an initializer does not establish a member semantic scope), so without
        // resolving the declared field first, accessibilityFilter=public would incorrectly include
        // these private-field diagnostics. Compiler only emits CS0169/CS0414 for non-public fields.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private int privateUnused;
                private int privateAssignedUnused = 1;
                public void Pub() { int inPublic; }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS0169", "CS0414", "CS0168" },
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = new[] { "CS0169", "CS0414", "CS0168" },
            AccessibilityFilter = "private"
        });

        Assert.True(pub.Success);
        Assert.True(priv.Success);
        // Public: only the method-body CS0168 site — not the private-field declaration diagnostics.
        Assert.Equal(new[] { "inPublic" }, VariableNames(pub.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("C.privateUnused", VariableNames(pub.Data!));
        Assert.DoesNotContain("C.privateAssignedUnused", VariableNames(pub.Data!));
        // Private: both field diagnostics; not the public method's unused local.
        Assert.Equal(
            new[] { "C.privateAssignedUnused", "C.privateUnused" },
            VariableNames(priv.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("inPublic", VariableNames(priv.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_FieldTypeAndModifierDiagnosticsUseFieldAccessibility()
    {
        // CS0246 (unknown field type) / CS0106 (invalid modifier) attach to the field declaration's
        // type / modifier, whose ancestors are VariableDeclarationSyntax / FieldDeclarationSyntax but not
        // the declarator. GetDeclaredSymbol yields no field for those container nodes, so without
        // resolving through a declarator the site would fall out to the public containing type.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private MissingPrivate privateField;
                public MissingPublic publicField;
                private event MissingHandler privateEvent;
                private virtual int virtualField;
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0246", "CS0106" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.True(pub.Success);
        Assert.True(priv.Success);
        Assert.Equal(4, omitted.Data!.TotalCount);
        Assert.Equal(new[] { "MissingPublic" }, VariableNames(pub.Data!));
        Assert.Equal(
            new[] { "MissingHandler", "MissingPrivate", "virtual" },
            VariableNames(priv.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DirectiveTriviaBeforeDeclarationDoesNotInheritIt()
    {
        // FindToken attributes a position in leading trivia to the following token, so a #warning
        // directive immediately before `public class` must not resolve to that class: namespace-level
        // (and compilation-unit-level) sites never match a named filter.
        await using var workspace = await TempWorkspace.CreateAsync("""
            #warning TopLevel
            public class C { public void M() { int inPublic; } }

            namespace App
            {
                #warning InNamespace
                public class D { }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1030", "CS0168" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ids
        });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            SeverityFilter = "All",
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.Diagnostics.Count(d => d.Id == "CS1030"));
        Assert.True(pub.Success);
        Assert.DoesNotContain(pub.Data!.Diagnostics, d => d.Id == "CS1030");
        Assert.Equal(new[] { "inPublic" }, VariableNames(pub.Data!));
        Assert.Equal(1, pub.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DocumentationCommentDiagnosticsUseDocumentedMember()
    {
        // Documentation comment trivia belongs to the declaration it documents, so CS1574 on a doc
        // comment cref is judged by that member's accessibility (not the enclosing public type).
        await using var workspace = await TempWorkspace.CreateAsync(
            """
            /// <summary>C</summary>
            public class C
            {
                /// <see cref="NopePublic"/>
                public void Pub() { }

                /// <see cref="NopePrivate"/>
                private void Priv() { }
            }
            """,
            extraProperties: "<GenerateDocumentationFile>true</GenerateDocumentationFile>");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1574" };

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(pub.Success);
        Assert.True(priv.Success);
        Assert.Equal(new[] { "NopePublic" }, VariableNames(pub.Data!));
        Assert.Equal(new[] { "NopePrivate" }, VariableNames(priv.Data!));
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_AnonymousObjectAndTupleMembersUseEnclosingMember()
    {
        // An anonymous-object member declarator (`P = Missing`) declares a synthesized public property
        // of the anonymous type, and a named tuple argument declares a public tuple element field; the
        // site must look through both to the enclosing source member's accessibility.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private void Priv()
                {
                    var anon = new { P = MissingAnonPrivate };
                    var projected = new { MissingProjectedPrivate };
                    var tuple = (a: MissingTuplePrivate, b: 1);
                }

                public void Pub()
                {
                    var anon = new { P = MissingAnonPublic };
                }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0103" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(4, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Equal(new[] { "MissingAnonPublic" }, VariableNames(pub.Data!));
        Assert.True(priv.Success);
        Assert.Equal(
            new[] { "MissingAnonPrivate", "MissingProjectedPrivate", "MissingTuplePrivate" },
            VariableNames(priv.Data!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ZeroWidthMissingSemicolonUsesFieldAccessibility()
    {
        // CS1002 is zero-width at the end of `private int field` (declarator Span excludes End).
        // FieldDeclaration.Span still covers the site (missing semicolon token extends End past the
        // diagnostic), and GetDeclaredSymbol bridges to the private field — pin that so a future
        // span-tightening change cannot attribute CS1002 to the public containing type.
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private int field
                public void M() { }
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1002" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.True(omitted.Data!.TotalCount >= 1);
        Assert.True(pub.Success);
        Assert.DoesNotContain(pub.Data!.Diagnostics, d => d.Id == "CS1002");
        Assert.True(priv.Success);
        Assert.Contains(priv.Data!.Diagnostics, d => d.Id == "CS1002");
        Assert.Equal(omitted.Data.TotalCount, priv.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ZeroWidthMissingSemicolonAtEofUsesFieldAccessibility()
    {
        // `private int field` at EOF with no trailing newline and no closing brace: CS1002 is zero-width
        // at EOF, so FindToken lands on the EOF token (whose ancestors cover nothing) and an
        // uninitialized field has no member scope for GetEnclosingSymbol. The site must resolve through
        // the preceding declaration (the private field), not the public containing type.
        await using var workspace = await TempWorkspace.CreateAsync(
            "public class C\n{\n    private int field");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1002" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(1, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.DoesNotContain(pub.Data!.Diagnostics, d => d.Id == "CS1002");
        Assert.True(priv.Success);
        Assert.Single(priv.Data!.Diagnostics, d => d.Id == "CS1002");

        // The site itself resolves to the private field.
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var cs1002 = Assert.Single((await tree.GetRootAsync()).GetDiagnostics(), d => d.Id == "CS1002");
        Assert.Equal(0, cs1002.Location.SourceSpan.Length);
        Assert.Equal(tree.Length, cs1002.Location.SourceSpan.Start);
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var site = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, cs1002.Location.SourceSpan, cs1002.Id, CancellationToken.None);
        var field = Assert.IsAssignableFrom<IFieldSymbol>(site);
        Assert.Equal("field", field.Name);
        Assert.Equal(Accessibility.Private, field.DeclaredAccessibility);

        // Without a diagnostic id the zero-width EOF boundary still resolves through the preceding
        // unterminated declaration.
        var positionalSite = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, cs1002.Location.SourceSpan, null, CancellationToken.None);
        Assert.Equal("field", Assert.IsAssignableFrom<IFieldSymbol>(positionalSite).Name);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_MissingCloseBraceAtEofAfterCompleteMemberUsesTypeAccessibility()
    {
        // CS1513 for the class's missing `}` is zero-width at EOF right after the private method's own
        // (present) `}`. Only a declaration ending in a missing token claims an EOF boundary diagnostic,
        // so the site is the public class, not the complete private method preceding it.
        await using var workspace = await TempWorkspace.CreateAsync(
            "public class C\n{\n    private void M() { }");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1513" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(1, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Single(pub.Data!.Diagnostics, d => d.Id == "CS1513");
        Assert.True(priv.Success);
        Assert.DoesNotContain(priv.Data!.Diagnostics, d => d.Id == "CS1513");

        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var cs1513 = Assert.Single((await tree.GetRootAsync()).GetDiagnostics(), d => d.Id == "CS1513");
        Assert.Equal(new TextSpan(tree.Length, 0), cs1513.Location.SourceSpan);
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var site = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, cs1513.Location.SourceSpan, cs1513.Id, CancellationToken.None);
        var type = Assert.IsAssignableFrom<INamedTypeSymbol>(site);
        Assert.Equal("C", type.Name);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DelegateParameterUsesDelegateAccessibility()
    {
        // CS0246 on a delegate parameter declares the parameter whose containing symbol is the
        // synthesized public Invoke method; DeclaringSiteSymbol must look through to the private
        // delegate type (not stop at Invoke).
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class C
            {
                private delegate void PrivateDel(MissingPrivate value);
                public delegate void PublicDel(MissingPublic value);
            }
            """);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0246" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Equal(new[] { "MissingPublic" }, VariableNames(pub.Data!));
        Assert.True(priv.Success);
        Assert.Equal(new[] { "MissingPrivate" }, VariableNames(priv.Data!));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDiagnostics_AccessibilityFilter_TopLevelStatementSitesNeverMatch(bool declarePublicProgram)
    {
        // A diagnostic in top-level statements (including inside a top-level local function or lambda)
        // has no declared member or type around it: GetEnclosingSymbol returns the implicitly declared
        // `<Main>$` entry point (private, on the synthesized internal Program type). DeclaringSiteSymbol
        // must treat that as a namespace-level site so no named filter matches it, even when the user
        // declares `public partial class Program`.
        var programDeclaration = declarePublicProgram ? "public partial class Program { }\n" : "";
        await using var workspace = await TempWorkspace.CreateAsync($$"""
            Missing();
            void Local() { MissingInLocal(); }
            System.Action f = () => MissingInLambda();

            public class C
            {
                public void M() { MissingPublic(); }
                private void P() { MissingPrivate(); }
                internal void I() { MissingInternal(); }
            }
            {{programDeclaration}}
            """, outputType: "Exe");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0103" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });
        var intern = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "internal"
        });

        Assert.True(omitted.Success);
        Assert.Equal(6, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Equal(new[] { "MissingPublic" }, VariableNames(pub.Data!));
        Assert.True(priv.Success);
        Assert.Equal(new[] { "MissingPrivate" }, VariableNames(priv.Data!));
        Assert.True(intern.Success);
        Assert.Equal(new[] { "MissingInternal" }, VariableNames(intern.Data!));

        // The raw enclosing symbol of `Missing();` is the synthesized entry point; the declaring site
        // is namespace-level.
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var site = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, new TextSpan(0, "Missing".Length), "CS0103", CancellationToken.None);
        var entryPoint = Assert.IsAssignableFrom<IMethodSymbol>(site);
        Assert.Equal(WellKnownMemberNames.TopLevelStatementsEntryPointMethodName, entryPoint.Name);
        Assert.IsAssignableFrom<INamespaceSymbol>(DeclaringSiteSymbol.Resolve(site));
    }

    [SkippableTheory]
    [InlineData("public class C { private int field}\n")]
    [InlineData("public class C { private int field }\n")]
    public async Task GetDiagnostics_AccessibilityFilter_MissingSemicolonReportedOnClosingBraceUsesFieldAccessibility(string source)
    {
        // `private int field}` (or `field }` on one line): Roslyn reports CS1002 for the field's missing
        // `;` on the containing type's `}` (a one-character span past the field's own span), so a
        // positional walk lands on the public class. The diagnostic is carried by the field's missing
        // `;` token and must be judged by the private field.
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1002" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(1, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.DoesNotContain(pub.Data!.Diagnostics, d => d.Id == "CS1002");
        Assert.True(priv.Success);
        Assert.Single(priv.Data!.Diagnostics, d => d.Id == "CS1002");

        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var cs1002 = Assert.Single((await tree.GetRootAsync()).GetDiagnostics(), d => d.Id == "CS1002");
        var brace = (await tree.GetTextAsync()).ToString().IndexOf('}');
        Assert.Equal(new TextSpan(brace, 1), cs1002.Location.SourceSpan);
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var site = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, cs1002.Location.SourceSpan, cs1002.Id, CancellationToken.None);
        var field = Assert.IsAssignableFrom<IFieldSymbol>(site);
        Assert.Equal("field", field.Name);

        // Control: any other diagnostic on that same `}` (not carried by the missing `;`) is a site on
        // the brace itself and still belongs to the type.
        foreach (var otherId in new string?[] { null, "CS0000", "CS1513" })
        {
            var braceSite = await resolver.GetDeclaredOrEnclosingSymbolAsync(
                tree, document, new TextSpan(brace, 1), otherId, CancellationToken.None);
            var type = Assert.IsAssignableFrom<INamedTypeSymbol>(braceSite);
            Assert.Equal("C", type.Name);
        }
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DiagnosticOnMethodClosingBraceUsesMethodAccessibility()
    {
        // Control on real (non-missing) syntax: CS0177 is reported on the private method's closing `}`
        // (out parameter not assigned before control leaves), immediately before the public type's
        // `}`. It is not a missing-syntax diagnostic, so positional resolution keeps it on the method.
        await using var workspace = await TempWorkspace.CreateAsync(
            "public class C { private void M(out int x) {}}\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0177" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(1, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Empty(pub.Data!.Diagnostics);
        Assert.True(priv.Success);
        Assert.Single(priv.Data!.Diagnostics, d => d.Id == "CS0177");
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_MissingInitializerExpressionReportedOnClosingBraceUsesFieldAccessibility()
    {
        // `private int field = }`: CS1525 (invalid expression term '}') and CS1002 are both reported on
        // the type's `}` but are carried by the field's missing initializer expression and missing `;`,
        // so both are judged by the private field.
        await using var workspace = await TempWorkspace.CreateAsync("public class C { private int field = }\n");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1525", "CS1002" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Empty(pub.Data!.Diagnostics);
        Assert.True(priv.Success);
        Assert.Equal(2, priv.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_MissingTypeCloseBraceAtEofAfterUnterminatedFieldUsesTypeAccessibility()
    {
        // `private int field` at EOF: CS1002 (field's missing `;`) and CS1513 (class's missing `}`) are
        // both zero-width at EOF. Each is judged by the declaration whose missing token carries it.
        await using var workspace = await TempWorkspace.CreateAsync(
            "public class C\n{\n    private int field");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1002", "CS1513" };

        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(pub.Success);
        Assert.Equal(new[] { "CS1513" }, pub.Data!.Diagnostics.Select(d => d.Id).ToArray());
        Assert.True(priv.Success);
        Assert.Equal(new[] { "CS1002" }, priv.Data!.Diagnostics.Select(d => d.Id).ToArray());
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_NestedMissingCloseBracesAtEofUseEachTypeAccessibility()
    {
        // `public class C { private class D {` at EOF: Roslyn reports two CS1513 with the same id and the
        // same zero-width span at EOF, one carried by each type's missing `}`. The two are equal values
        // (Diagnostic identity is not preserved), so each occurrence must keep its own carrier: the
        // public filter gets exactly C's, the private filter exactly D's.
        await using var workspace = await TempWorkspace.CreateAsync("public class C { private class D {");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1513" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        Assert.Equal(1, pub.Data!.TotalCount);
        Assert.Single(pub.Data.Diagnostics, d => d.Id == "CS1513");
        Assert.True(priv.Success);
        Assert.Equal(1, priv.Data!.TotalCount);
        Assert.Single(priv.Data.Diagnostics, d => d.Id == "CS1513");

        // Both CS1513 share one span; occurrence k resolves to the carrier of the k-th one in tree
        // diagnostic order (the inner type's `}` comes first).
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var cs1513 = (await tree.GetRootAsync()).GetDiagnostics().Where(d => d.Id == "CS1513").ToArray();
        Assert.Equal(2, cs1513.Length);
        Assert.All(cs1513, d => Assert.Equal(new TextSpan(tree.Length, 0), d.Location.SourceSpan));
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var span = cs1513[0].Location.SourceSpan;
        var first = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, span, "CS1513", 0, CancellationToken.None);
        var second = await resolver.GetDeclaredOrEnclosingSymbolAsync(
            tree, document, span, "CS1513", 1, CancellationToken.None);
        Assert.Equal("D", Assert.IsAssignableFrom<INamedTypeSymbol>(first).Name);
        Assert.Equal("C", Assert.IsAssignableFrom<INamedTypeSymbol>(second).Name);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ThreeNestedMissingCloseBracesAtEofUseEachTypeAccessibility()
    {
        // Three collocated CS1513 at EOF, each carried by a different type's missing `}`.
        await using var workspace = await TempWorkspace.CreateAsync(
            "public class C { private class D { internal class E {");
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1513" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        Assert.True(omitted.Success);
        Assert.Equal(3, omitted.Data!.TotalCount);

        foreach (var filter in new[] { "public", "private", "internal" })
        {
            var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = ids,
                AccessibilityFilter = filter
            });
            Assert.True(filtered.Success);
            Assert.Equal(1, filtered.Data!.TotalCount);
        }

        var prot = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "protected"
        });
        Assert.True(prot.Success);
        Assert.Equal(0, prot.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_ManyMalformedFieldsUseEachFieldAccessibility()
    {
        // Many fields each missing `;` (CS1002 per field, carried by that field's missing `;`), the
        // last one reported on the type's `}`, and the type itself unterminated at EOF (CS1513). The
        // per-tree carrier index attributes every occurrence to its own declaration.
        const int pairs = 150;
        var builder = new System.Text.StringBuilder("public class C\n{\n");
        for (var i = 0; i < pairs; i++)
        {
            builder.Append("    private int p").Append(i).Append('\n');
            builder.Append("    public int q").Append(i).Append('\n');
        }

        builder.Append("    private class Inner { private int last}\n");
        await using var workspace = await TempWorkspace.CreateAsync(builder.ToString());
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1002", "CS1513" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        var pub = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "public"
        });
        var priv = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "private"
        });

        Assert.True(omitted.Success);
        Assert.Equal(2 * pairs + 2, omitted.Data!.TotalCount);
        Assert.True(pub.Success);
        // q0..q149 (CS1002) plus C's missing `}` (CS1513).
        Assert.Equal(pairs, pub.Data!.Diagnostics.Count(d => d.Id == "CS1002"));
        Assert.Single(pub.Data.Diagnostics, d => d.Id == "CS1513");
        Assert.Equal(pairs + 1, pub.Data.TotalCount);
        Assert.True(priv.Success);
        // p0..p149 plus `last` (CS1002 on Inner's `}`).
        Assert.Equal(pairs + 1, priv.Data!.Diagnostics.Count(d => d.Id == "CS1002"));
        Assert.DoesNotContain(priv.Data.Diagnostics, d => d.Id == "CS1513");
        Assert.Equal(pairs + 1, priv.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DeepNestedMissingCloseBracesScaleLinearly()
    {
        // ~40 nested unterminated types at EOF: each level contributes one collocated CS1513. The
        // carrier index must attribute each occurrence to its own type without O(N²) subtree
        // re-enumeration while building the index (regression for the linear GetSyntaxDiagnosticCarriers
        // walk). Accessibility filters still isolate public / private / internal carriers.
        const int depth = 40;
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            var accessibility = (i % 3) switch
            {
                0 => "public",
                1 => "private",
                _ => "internal"
            };
            builder.Append(accessibility).Append(" class C").Append(i).Append(" { ");
        }

        await using var workspace = await TempWorkspace.CreateAsync(builder.ToString());
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1513" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        Assert.True(omitted.Success);
        Assert.Equal(depth, omitted.Data!.TotalCount);

        var expectedPublic = (depth + 2) / 3;   // i % 3 == 0
        var expectedPrivate = (depth + 1) / 3;  // i % 3 == 1
        var expectedInternal = depth / 3;       // i % 3 == 2

        foreach (var (filter, expected) in new[]
                 {
                     ("public", expectedPublic),
                     ("private", expectedPrivate),
                     ("internal", expectedInternal)
                 })
        {
            var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = ids,
                AccessibilityFilter = filter
            });
            Assert.True(filtered.Success);
            Assert.Equal(expected, filtered.Data!.TotalCount);
            Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS1513", d.Id));
        }

        var prot = await operation.ExecuteAsync(new GetDiagnosticsParams
        {
            DiagnosticIds = ids,
            AccessibilityFilter = "protected"
        });
        Assert.True(prot.Success);
        Assert.Equal(0, prot.Data!.TotalCount);

        // Occurrence order: innermost missing `}` first (C{depth-1} … C0), matching tree diagnostic order.
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var cs1513 = (await tree.GetRootAsync()).GetDiagnostics().Where(d => d.Id == "CS1513").ToArray();
        Assert.Equal(depth, cs1513.Length);
        var span = cs1513[0].Location.SourceSpan;
        Assert.All(cs1513, d => Assert.Equal(span, d.Location.SourceSpan));
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        for (var occurrence = 0; occurrence < depth; occurrence++)
        {
            var carrier = await resolver.GetDeclaredOrEnclosingSymbolAsync(
                tree, document, span, "CS1513", occurrence, CancellationToken.None);
            var expectedName = "C" + (depth - 1 - occurrence);
            Assert.Equal(expectedName, Assert.IsAssignableFrom<INamedTypeSymbol>(carrier).Name);
        }
    }

    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DeepNestedUnterminatedInvocationsScaleLinearly()
    {
        // Many nested unterminated invocation statements `F(F(...F(0;` inside one method: each missing
        // `)` reports a collocated CS1026 at the `;`, and every missing token's ancestor chain (argument
        // lists and invocations) leads to the same method. The carrier index memoizes enclosing declarations per
        // node, so resolving each missing `)` does not re-walk the shared chain (regression for the
        // quadratic GetEnclosingDeclaredSymbol walk). Each method's diagnostics keep its accessibility.
        const int privateDepth = 64;
        const int publicDepth = 48;
        var source =
            "public class C\n{\n" +
            "    private static int F(int x) => x;\n" +
            "    private void Hidden()\n    {\n        " +
            string.Concat(Enumerable.Repeat("F(", privateDepth)) + "0;\n    }\n" +
            "    public void Visible()\n    {\n        " +
            string.Concat(Enumerable.Repeat("F(", publicDepth)) + "0;\n    }\n" +
            "}\n";
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS1026" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        Assert.True(omitted.Success);
        Assert.Equal(privateDepth + publicDepth, omitted.Data!.TotalCount);

        foreach (var (filter, expected) in new[]
                 {
                     ("private", privateDepth),
                     ("public", publicDepth),
                     ("internal", 0),
                     ("protected", 0)
                 })
        {
            var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = ids,
                AccessibilityFilter = filter
            });
            Assert.True(filtered.Success);
            Assert.Equal(expected, filtered.Data!.TotalCount);
            Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS1026", d.Id));
        }

        // Every collocated occurrence in each method resolves to that method.
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var groups = (await tree.GetRootAsync()).GetDiagnostics()
            .Where(d => d.Id == "CS1026")
            .GroupBy(d => d.Location.SourceSpan)
            .ToArray();
        Assert.Equal(2, groups.Length);
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        foreach (var (group, expectedName, expectedCount) in new[]
                 {
                     (groups[0], "Hidden", privateDepth),
                     (groups[1], "Visible", publicDepth)
                 })
        {
            Assert.Equal(expectedCount, group.Count());
            for (var occurrence = 0; occurrence < expectedCount; occurrence++)
            {
                var carrier = await resolver.GetDeclaredOrEnclosingSymbolAsync(
                    tree, document, group.Key, "CS1026", occurrence, CancellationToken.None);
                Assert.Equal(expectedName, Assert.IsAssignableFrom<IMethodSymbol>(carrier).Name);
            }
        }
    }


    [SkippableFact]
    public async Task GetDiagnostics_AccessibilityFilter_DeepNestedUnresolvedInvocationsScaleLinearly()
    {
        // Syntactically valid nested F(Missing0, F(Missing1, ...)) — each MissingN is CS0103.
        // With accessibilityFilter set, ordinary semantic diagnostics take the positional ancestor
        // walk (not the missing-syntax carrier index). Memoizing nearest eligible declarations keeps
        // shared invocation/argument ancestors from being re-bound O(N²) times (regression for the
        // positional GetDeclaredOrEnclosingSymbolAsync walk). Each method's CS0103 sites keep its
        // accessibility. Scratch before/after GetDeclaredSymbol steps: n=10/40/160 → 205/2620/39280
        // (quadratic) without memo vs 52/202/802 (linear) with memo.
        const int privateDepth = 64;
        const int publicDepth = 48;

        static string NestUnresolved(int depth)
        {
            var inner = "0";
            for (var i = depth - 1; i >= 0; i--)
                inner = $"F(Missing{i}, {inner})";
            return inner;
        }

        var source =
            "public class C\n{\n" +
            "    private static int F(int a, int b) => a;\n" +
            "    private void Hidden()\n    {\n        " +
            NestUnresolved(privateDepth) + ";\n    }\n" +
            "    public void Visible()\n    {\n        " +
            NestUnresolved(publicDepth) + ";\n    }\n" +
            "}\n";
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetDiagnosticsOperation(workspace.Context);
        var ids = new[] { "CS0103" };

        var omitted = await operation.ExecuteAsync(new GetDiagnosticsParams { DiagnosticIds = ids });
        Assert.True(omitted.Success);
        Assert.Equal(privateDepth + publicDepth, omitted.Data!.TotalCount);

        foreach (var (filter, expected) in new[]
                 {
                     ("private", privateDepth),
                     ("public", publicDepth),
                     ("internal", 0),
                     ("protected", 0)
                 })
        {
            var filtered = await operation.ExecuteAsync(new GetDiagnosticsParams
            {
                DiagnosticIds = ids,
                AccessibilityFilter = filter
            });
            Assert.True(filtered.Success);
            Assert.Equal(expected, filtered.Data!.TotalCount);
            Assert.All(filtered.Data.Diagnostics, d => Assert.Equal("CS0103", d.Id));
        }

        // Every unresolved identifier in each method resolves positionally to that method.
        var document = workspace.Context.GetDocumentByPath(workspace.SourcePath)!;
        var tree = (await document.GetSyntaxTreeAsync())!;
        var resolver = new SiteEnclosingSymbolResolver(workspace.Context);
        var privateSites = 0;
        var publicSites = 0;
        var root = await tree.GetRootAsync();
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!id.Identifier.ValueText.StartsWith("Missing", StringComparison.Ordinal))
                continue;
            var carrier = await resolver.GetDeclaredOrEnclosingSymbolAsync(
                tree, document, id.Span, "CS0103", occurrence: 0, CancellationToken.None);
            switch (Assert.IsAssignableFrom<IMethodSymbol>(carrier).Name)
            {
                case "Hidden":
                    privateSites++;
                    break;
                case "Visible":
                    publicSites++;
                    break;
                default:
                    Assert.Fail($"Unexpected enclosing method {carrier}");
                    break;
            }
        }

        Assert.Equal(privateDepth, privateSites);
        Assert.Equal(publicDepth, publicSites);
    }

    #endregion


    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string ProjectPath => Path.Combine(DirectoryPath, "TestApp.csproj");

        public static async Task<TempWorkspace> CreateAsync(
            string source,
            string fileName = "Foo.cs",
            string? outputType = null,
            IReadOnlyDictionary<string, string>? extraFiles = null,
            string extraProperties = "")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnostics_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var outputTypeElement = outputType == null ? "" : $"<OutputType>{outputType}</OutputType>";
            await File.WriteAllTextAsync(projectPath, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    {outputTypeElement}
                    {extraProperties}
                  </PropertyGroup>
                </Project>
                """);

            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, source);

            if (extraFiles != null)
            {
                foreach (var (extraName, extraSource) in extraFiles)
                    await File.WriteAllTextAsync(Path.Combine(directory, extraName), extraSource);
            }

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
            string libSource = "#warning LibWarning\nnamespace Lib { public class Animal {} }\n",
            string appSource = "#warning AppWarning\nnamespace App { public class Dog : Lib.Animal {} }\n")
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetDiagnosticsXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSourcePath = Path.Combine(libDir, "Animal.cs");
            var appSourcePath = Path.Combine(appDir, "Dog.cs");

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

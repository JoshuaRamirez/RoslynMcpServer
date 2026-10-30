using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Query;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Query;

/// <summary>
/// Operation-level tests for <see cref="GetSymbolInfoOperation"/> optional <c>maxResults</c>,
/// <c>kindFilter</c>, <c>accessibilityFilter</c>, <c>nameFilter</c>, and <c>namespaceFilter</c>
/// (peer of <see cref="GetTypeHierarchyOperation"/> / <c>search_symbols</c> / <c>get_document_outline</c>).
/// </summary>
public class GetSymbolInfoOperationTests
{
    private const string WidgetSource = """
        public interface IFirst { }
        public interface ISecond { }

        public class Widget : IFirst, ISecond
        {
            public const int Limit = 10;
            public int Alpha;
            public string Beta { get; set; } = "";
            public void Gamma() { }
            public int Delta(int a, string b, bool c) => a;
            public event System.EventHandler? Epsilon;
            public class Nested { }
        }
        """;

    private const string GadgetSource = """
        public interface IContract
        {
            void Run();
        }

        public class Gadget
        {
            public int PubField;
            internal int InternalField;
            protected int ProtectedField;
            private int privateField;
            int implicitField;
            protected internal void ProtectedInternalMethod() { }
            private protected void PrivateProtectedMethod() { }
            public void PubMethod() { }
            void ImplicitMethod() { }
            public string PubProp { get; set; } = "";
            class ImplicitNested { }
        }
        """;

    // Same members as WidgetSource, but declared under App.Services for namespaceFilter tests.
    private const string NamespacedWidgetSource = """
        namespace App.Services
        {
            public interface IFirst { }
            public interface ISecond { }

            public class Widget : IFirst, ISecond
            {
                public const int Limit = 10;
                public int Alpha;
                public string Beta { get; set; } = "";
                public void Gamma() { }
                public int Delta(int a, string b, bool c) => a;
                public event System.EventHandler? Epsilon;
                public class Nested { }
            }
        }
        """;

    #region Input Validation

    [Fact]
    public void MaxResults_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.MaxResults);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Validate_MaxResultsBelowOne_ThrowsMissingRequiredParam(int maxResults)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                MaxResults = maxResults
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Equal("maxResults must be >= 1.", ex.Message);
    }

    #endregion

    #region Members truncation

    [SkippableFact]
    public async Task GetSymbolInfo_OmittedMaxResults_ReturnsFullMembersNotTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_MaxResultsTruncates_ReturnsFirstNMembersAndSetsTruncated()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(full.Data.Members);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(full.Data.Members.Take(2).ToArray(), capped.Data.Members.ToArray());
        Assert.Equal(full.Data.TotalCount, capped.Data.TotalCount);
        Assert.Equal(7, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData(7)]
    [InlineData(50)]
    public async Task GetSymbolInfo_MaxResultsAtOrAboveCount_NotTruncated(int maxResults)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget"
        });
        Assert.True(full.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(full.Data.Members);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = maxResults
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(full.Data.Members.ToArray(), capped.Data.Members.ToArray());
        Assert.Equal(7, capped.Data.TotalCount);
        Assert.False(capped.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_MaxResultsDoesNotCapInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Single(capped.Data.Members);
        Assert.True(capped.Data.Truncated);
        Assert.NotNull(capped.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, capped.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_MaxResultsHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            MaxResults = 1
        });

        Assert.True(full.Success);
        Assert.True(capped.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(capped.Data);
        Assert.Null(capped.Data.Members);
        Assert.Null(capped.Data.TotalCount);
        Assert.False(capped.Data.Truncated);
        Assert.NotNull(capped.Data.Parameters);
        Assert.Equal(3, capped.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, capped.Data.Signature);
        Assert.Equal(full.Data.ReturnType, capped.Data.ReturnType);
    }

    #endregion

    #region kindFilter

    [Fact]
    public void KindFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.KindFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSymbolInfo_OmittedOrBlankKindFilter_ReturnsFullMembers(string? kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterMethod_ReturnsOnlyMethods()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(2, result.Data.Members.Count);
        Assert.Equal(2, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(result.Data.Members, m => m.Contains("Gamma", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("Delta", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Alpha", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Beta", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("Property", "Beta")]
    [InlineData("Field", "Alpha")]
    [InlineData("Event", "Epsilon")]
    [InlineData("Constant", "Limit")]
    [InlineData("Class", "Nested")]
    public async Task GetSymbolInfo_KindFilter_MatchesMappedKind(string kindFilter, string expectedName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(expectedName, result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterCaseInsensitiveAndTrimmed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "  pRoPeRtY  "
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Contains("Beta", result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("Local")]
    [InlineData("Parameter")]
    [InlineData("Namespace")]
    [InlineData("Struct")]
    public async Task GetSymbolInfo_KindFilterNeverMatching_ReturnsEmptyMembers(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = kindFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotNull(filtered.Data.Members);
        Assert.Equal(2, filtered.Data.Members.Count);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method",
            MaxResults = 1
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Single(capped.Data.Members);
        Assert.Equal(2, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(filtered.Data.Members[0], capped.Data.Members[0]);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_KindFilterDoesNotFilterInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            KindFilter = "Method"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, result.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_KindFilterHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            KindFilter = "Property"
        });

        Assert.True(full.Success);
        Assert.True(filtered.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(filtered.Data);
        Assert.Null(filtered.Data.Members);
        Assert.Null(filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.NotNull(filtered.Data.Parameters);
        Assert.Equal(3, filtered.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, filtered.Data.Signature);
    }

    [SkippableTheory]
    [InlineData("Nope")]
    [InlineData("6")]
    [InlineData("Class, Method")]
    public async Task Validate_InvalidKindFilter_ThrowsInvalidSymbolKind(string kindFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = "Widget",
                KindFilter = kindFilter
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("kindFilter", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region accessibilityFilter

    [Fact]
    public void AccessibilityFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.AccessibilityFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSymbolInfo_OmittedOrBlankAccessibilityFilter_ReturnsFullMembers(string? accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(11, result.Data.Members.Count);
        Assert.Equal(11, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterPublic_ReturnsOnlyPublicMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = "public"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(3, result.Data.Members.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(result.Data.Members, m => m.Contains("PubField", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("PubMethod", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("PubProp", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterPrivate_CountsImplicitDefaults()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(4, result.Data.Members.Count);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.Contains(result.Data.Members, m => m.Contains("privateField", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("implicitField", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("ImplicitMethod", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("ImplicitNested", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("PrivateProtectedMethod", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("internal", "InternalField")]
    [InlineData("protected", "ProtectedField")]
    [InlineData("protected internal", "ProtectedInternalMethod")]
    [InlineData("private protected", "PrivateProtectedMethod")]
    [InlineData("internal protected", "ProtectedInternalMethod")]
    [InlineData("protected private", "PrivateProtectedMethod")]
    [InlineData("  PROTECTED   Internal  ", "ProtectedInternalMethod")]
    public async Task GetSymbolInfo_AccessibilityFilter_MatchesExactly(string accessibilityFilter, string expectedName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(expectedName, result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterNoMatch_ReturnsEmptyMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("public", 1)]
    [InlineData("private", 0)]
    public async Task GetSymbolInfo_AccessibilityFilterOnInterface_MembersAreImplicitlyPublic(string accessibilityFilter, int expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "IContract",
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(expected, result.Data.Members.Count);
        Assert.Equal(expected, result.Data.TotalCount);
    }

    [SkippableTheory]
    [InlineData("Method", "public", "PubMethod")]
    [InlineData("Method", "private", "ImplicitMethod")]
    [InlineData("Field", "protected", "ProtectedField")]
    [InlineData("Property", "public", "PubProp")]
    public async Task GetSymbolInfo_AccessibilityFilterCombinedWithKindFilter_RequiresBoth(
        string kindFilter, string accessibilityFilter, string expectedName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = kindFilter,
            AccessibilityFilter = accessibilityFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Contains(expectedName, result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterCombinedWithKindFilter_NoOverlapReturnsEmpty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = "Property",
            AccessibilityFilter = "internal"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = "private"
        });
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotNull(filtered.Data.Members);
        Assert.Equal(4, filtered.Data.Members.Count);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = "private",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(2, capped.Data.Members.Count);
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(filtered.Data.Members.Take(2).ToArray(), capped.Data.Members.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AccessibilityFilterDoesNotFilterInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            AccessibilityFilter = "private"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, result.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_AccessibilityFilterHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            AccessibilityFilter = "private"
        });

        Assert.True(full.Success);
        Assert.True(filtered.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(filtered.Data);
        Assert.Null(filtered.Data.Members);
        Assert.Null(filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.Equal("public", filtered.Data.Accessibility);
        Assert.NotNull(filtered.Data.Parameters);
        Assert.Equal(3, filtered.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, filtered.Data.Signature);
    }

    [SkippableTheory]
    [InlineData("Widget", "friend")]
    [InlineData("Widget", "ProtectedOrInternal")]
    [InlineData("Widget", "public, internal")]
    [InlineData("Delta", "nope")]
    public async Task Validate_InvalidAccessibilityFilter_ThrowsInvalidVisibility(string symbolName, string accessibilityFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                SymbolName = symbolName,
                AccessibilityFilter = accessibilityFilter
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(
            $"Invalid accessibilityFilter '{accessibilityFilter}'. Valid values: public, internal, protected, private, protected internal, private protected",
            ex.Message);
    }

    #endregion

    #region nameFilter

    [Fact]
    public void NameFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NameFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSymbolInfo_OmittedOrBlankNameFilter_ReturnsFullMembers(string? nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterSubstring_ReturnsMatchingMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "a"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        // Alpha, Beta, Gamma, Delta — not Limit / Epsilon / Nested (no 'a' in the simple name).
        Assert.Equal(4, result.Data.Members.Count);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(result.Data.Members, m => m.Contains("Alpha", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("Beta", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("Gamma", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("Delta", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Limit", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Epsilon", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("Nested", StringComparison.Ordinal));
    }

    [SkippableTheory]
    [InlineData("delta")]
    [InlineData("DELTA")]
    [InlineData("  Delta  ")]
    [InlineData("DeLtA")]
    public async Task GetSymbolInfo_NameFilter_IsCaseInsensitiveAndIgnoresSurroundingWhitespace(string nameFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Contains("Delta", result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterNoMatch_ReturnsEmptyMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "Zzz"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("Method", "pub", "PubMethod")]
    [InlineData("Field", "pub", "PubField")]
    [InlineData("Property", "prop", "PubProp")]
    public async Task GetSymbolInfo_NameFilterCombinedWithKindFilter_RequiresBoth(
        string kindFilter, string nameFilter, string expectedName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = kindFilter,
            NameFilter = nameFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Contains(expectedName, result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterCombinedWithAccessibilityFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            AccessibilityFilter = "private",
            NameFilter = "implicit"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        // private: privateField, implicitField, ImplicitMethod, ImplicitNested — name "implicit" → last three
        Assert.Equal(3, result.Data.Members.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.Contains(result.Data.Members, m => m.Contains("implicitField", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("ImplicitMethod", StringComparison.Ordinal));
        Assert.Contains(result.Data.Members, m => m.Contains("ImplicitNested", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Data.Members, m => m.Contains("privateField", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterCombinedWithKindAndAccessibility_RequiresAll()
    {
        await using var workspace = await TempWorkspace.CreateAsync(GadgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = "Method",
            AccessibilityFilter = "private",
            NameFilter = "Implicit"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Contains("ImplicitMethod", result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "a"
        });
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotNull(filtered.Data.Members);
        Assert.Equal(4, filtered.Data.Members.Count);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "a",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(2, capped.Data.Members.Count);
        Assert.Equal(4, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(filtered.Data.Members.Take(2).ToArray(), capped.Data.Members.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NameFilterDoesNotFilterInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "Zzz"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Interfaces);
        Assert.Equal(new[] { "IFirst", "ISecond" }, result.Data.Interfaces.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_NameFilterHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            NameFilter = "Nope"
        });

        Assert.True(full.Success);
        Assert.True(filtered.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(filtered.Data);
        Assert.Null(filtered.Data.Members);
        Assert.Null(filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.NotNull(filtered.Data.Parameters);
        Assert.Equal(3, filtered.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, filtered.Data.Signature);
    }

    #endregion


    #region namespaceFilter

    [Fact]
    public void NamespaceFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.NamespaceFilter);
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("global::")]
    [InlineData("  global::  ")]
    public async Task GetSymbolInfo_OmittedOrBlankNamespaceFilter_ReturnsFullMembers(string? namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Services")]
    [InlineData("  App.Services  ")]
    [InlineData("global::App.Services")]
    [InlineData("  global::App.Services  ")]
    public async Task GetSymbolInfo_NamespaceFilterExactMatch_ReturnsMembers(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Equal("App.Services", result.Data.ContainingNamespace);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterNestedMatch_ParentKeepsMembers()
    {
        // App keeps members of a type in App.Services (nested-namespace matching).
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Equal(7, result.Data.Members.Count);
        Assert.Equal(7, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableTheory]
    [InlineData("App.Other")]
    [InlineData("App.Services.Orders")]
    [InlineData("App.ServicesExtra")]
    [InlineData("Other")]
    public async Task GetSymbolInfo_NamespaceFilterNoMatch_ReturnsEmptyMembers(string namespaceFilter)
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = namespaceFilter
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilter_GlobalNamespaceType_NamedFilterYieldsEmpty()
    {
        // WidgetSource is in the global namespace — members never match a named filter.
        await using var workspace = await TempWorkspace.CreateAsync(WidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterCombinedWithNameFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "a",
            NamespaceFilter = "App.Services"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        // Alpha, Beta, Gamma, Delta — same as nameFilter alone when namespace matches.
        Assert.Equal(4, result.Data.Members.Count);
        Assert.Equal(4, result.Data.TotalCount);

        var miss = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NameFilter = "a",
            NamespaceFilter = "App.Other"
        });
        Assert.True(miss.Success);
        Assert.NotNull(miss.Data);
        Assert.NotNull(miss.Data.Members);
        Assert.Empty(miss.Data.Members);
        Assert.Equal(0, miss.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterCombinedWithKindAndAccessibility_RequiresAll()
    {
        // Namespaced copy of Gadget-like type for combined filters.
        const string source = """
            namespace App.Services
            {
                public class Gadget
                {
                    public void PubMethod() { }
                    void ImplicitMethod() { }
                    public int PubField;
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = "Method",
            AccessibilityFilter = "private",
            NameFilter = "Implicit",
            NamespaceFilter = "App"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Single(result.Data.Members);
        Assert.Equal(1, result.Data.TotalCount);
        Assert.Contains("ImplicitMethod", result.Data.Members[0], StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AllMemberFiltersCombinedThenMaxResults_RequiresEveryFilter()
    {
        const string source = """
            namespace App.Services
            {
                public class Gadget
                {
                    public static void RunFirst() { }
                    public static void RunSecond() { }
                    public static void RunThird() { }
                    public void RunInstance() { }
                    private static void RunHidden() { }
                    public static int RunCount;
                    public static void Stop() { }
                }
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = "Method",
            AccessibilityFilter = "public",
            NameFilter = "run",
            NamespaceFilter = "App",
            StaticFilter = true,
            MaxResults = 2
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Equal(2, members.Count);
        Assert.Contains("RunFirst", members[0], StringComparison.Ordinal);
        Assert.Contains("RunSecond", members[1], StringComparison.Ordinal);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);

        var otherNamespace = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Gadget",
            KindFilter = "Method",
            AccessibilityFilter = "public",
            NameFilter = "run",
            NamespaceFilter = "App.Other",
            StaticFilter = true
        });

        Assert.True(otherNamespace.Success);
        Assert.Empty(otherNamespace.Data!.Members!);
        Assert.Equal(0, otherNamespace.Data.TotalCount);
        Assert.False(otherNamespace.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterThenMaxResults_TruncatesFilteredSet()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "App.Services"
        });
        Assert.True(filtered.Success);
        Assert.NotNull(filtered.Data);
        Assert.NotNull(filtered.Data.Members);
        Assert.Equal(7, filtered.Data.Members.Count);

        var capped = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "App.Services",
            MaxResults = 2
        });

        Assert.True(capped.Success);
        Assert.NotNull(capped.Data);
        Assert.NotNull(capped.Data.Members);
        Assert.Equal(2, capped.Data.Members.Count);
        Assert.Equal(7, capped.Data.TotalCount);
        Assert.True(capped.Data.Truncated);
        Assert.Equal(filtered.Data.Members.Take(2).ToArray(), capped.Data.Members.ToArray());
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterIsCaseSensitive()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "app.services"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
        Assert.Equal(0, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NamespaceFilterDoesNotFilterInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Widget",
            NamespaceFilter = "App.Other"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.Interfaces);
        Assert.Equal(new[] { "App.Services.IFirst", "App.Services.ISecond" }, result.Data.Interfaces.ToArray());
        Assert.NotNull(result.Data.Members);
        Assert.Empty(result.Data.Members);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_NonTypeSymbol_NamespaceFilterHasNoEffect()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NamespacedWidgetSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var full = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta"
        });
        var filtered = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Delta",
            NamespaceFilter = "Nope"
        });

        Assert.True(full.Success);
        Assert.True(filtered.Success);
        Assert.NotNull(full.Data);
        Assert.NotNull(filtered.Data);
        Assert.Null(filtered.Data.Members);
        Assert.Null(filtered.Data.TotalCount);
        Assert.False(filtered.Data.Truncated);
        Assert.NotNull(filtered.Data.Parameters);
        Assert.Equal(3, filtered.Data.Parameters.Count);
        Assert.Equal(full.Data.Signature, filtered.Data.Signature);
    }

    #endregion

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGetSymbolInfo_" + Guid.NewGuid().ToString("N"));
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

    #region staticFilter

    private const string StaticMixSource = """
        public class Mixer
        {
            public const int Limit = 10;
            public static int Counter;
            public int Instance;
            public static string Name { get; set; } = "";
            public string Label { get; set; } = "";
            public static void Create() { }
            public void Run() { }
            public static event System.EventHandler? Changed;
            public class Nested { }
            public static class Helpers { }
            public enum Mode { A }
            public struct Point { }
        }
        """;

    [Fact]
    public void StaticFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.StaticFilter);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_OmittedStaticFilter_ReturnsAllMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mixer"
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data?.Members);
        Assert.Equal(12, result.Data!.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterTrue_KeepsOnlyStaticMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mixer",
            StaticFilter = true
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Equal(6, members.Count);
        Assert.Equal(6, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        Assert.Contains(members, m => m.Contains("Limit", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Counter", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Create", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Changed", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Helpers", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Instance", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Run", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Nested", StringComparison.Ordinal));
        // Nested types are judged by INamedTypeSymbol.IsStatic: only a nested static class is static.
        Assert.DoesNotContain(members, m => m.Contains("Mode", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Point", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterFalse_KeepsOnlyInstanceMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mixer",
            StaticFilter = false
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Equal(6, members.Count);
        Assert.Equal(6, result.Data.TotalCount);
        Assert.Contains(members, m => m.Contains("Instance", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Label", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Run", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Nested", StringComparison.Ordinal));
        // Any nested type other than a static class (here an enum and a struct) counts as instance.
        Assert.Contains(members, m => m.Contains("Mode", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Point", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Limit", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Create", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Helpers", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterCombinedWithKindFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mixer",
            KindFilter = "Method",
            StaticFilter = true
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Single(members);
        Assert.Contains("Create", members[0], StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterAppliedBeforeMaxResults()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Mixer",
            StaticFilter = true,
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Members!.Count);
        Assert.Equal(6, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilter_NoEffectOnNonTypeSymbol()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Run",
            StaticFilter = true
        });

        Assert.True(result.Success);
        Assert.Null(result.Data!.Members);
        Assert.Null(result.Data.TotalCount);
    }


    private const string StaticExcludedKindsSource = """
        public interface IShape { int Area(); int Sides { get; } }
        public class Shape : IShape
        {
            static Shape() { }
            public Shape() { }
            ~Shape() { }
            public static Shape operator +(Shape a, Shape b) => a;
            public static implicit operator int(Shape s) => 0;
            public int this[int i] => i;
            int IShape.Area() => 0;
            int IShape.Sides => 4;
            public static int Total { get; set; }
            public static event System.EventHandler? Changed;
            public static void Make() { }
        }
        """;

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterTrue_NeverReturnsOperatorsConstructorsAccessorsOrExplicitImpls()
    {
        await using var workspace = await TempWorkspace.CreateAsync(StaticExcludedKindsSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var all = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape"
        });
        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Shape",
            StaticFilter = true
        });

        Assert.True(all.Success);
        Assert.True(result.Success);

        // Members never lists operators (incl. conversions), constructors (incl. the static
        // constructor), indexers, accessors, or explicit interface implementations, so
        // staticFilter = true cannot return them. A finalizer is listed and counts as instance.
        var allMembers = all.Data!.Members!;
        Assert.Equal(4, allMembers.Count);
        Assert.Contains(allMembers, m => m.Contains("~Shape", StringComparison.Ordinal));
        Assert.DoesNotContain(allMembers, m => m.Contains("operator", StringComparison.Ordinal));
        Assert.DoesNotContain(allMembers, m => m.Contains("Shape()", StringComparison.Ordinal) && !m.Contains('~'));
        Assert.DoesNotContain(allMembers, m => m.Contains("this[", StringComparison.Ordinal));
        Assert.DoesNotContain(allMembers, m => m.Contains("IShape.", StringComparison.Ordinal));
        Assert.DoesNotContain(allMembers, m => m.Contains("get_", StringComparison.Ordinal) || m.Contains("set_", StringComparison.Ordinal));
        Assert.DoesNotContain(allMembers, m => m.Contains("add_", StringComparison.Ordinal) || m.Contains("remove_", StringComparison.Ordinal));

        var members = result.Data!.Members!;
        Assert.Equal(3, members.Count);
        Assert.Equal(3, result.Data.TotalCount);
        Assert.Contains(members, m => m.Contains("Total", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Changed", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Make", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("~Shape", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_StaticFilterNoMatch_ReturnsEmptyMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class OnlyInstance
            {
                public int Value;
                public void Run() { }
            }
            """);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "OnlyInstance",
            StaticFilter = true
        });

        Assert.True(result.Success);
        Assert.NotNull(result.Data!.Members);
        Assert.Empty(result.Data.Members!);
        Assert.Equal(0, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
    }

    #endregion

    #region abstractFilter

    private const string AbstractMixSource = """
        public interface IShape
        {
            int Sides { get; }
            double Area();
            static abstract IShape Create();
            static virtual string Tag() => "s";
            string Describe() => "shape";
            event System.EventHandler? Changed;
            const int Max = 4;
        }

        public abstract class Template
        {
            public abstract void Run();
            public abstract int Count { get; }
            public abstract event System.EventHandler? Fired;
            public abstract int this[int i] { get; }
            public virtual void Hook() { }
            public void Execute() { }
            protected int state;
            public abstract class InnerBase { }
            public interface INested { }
            public static class Helpers { }
            public sealed class Leaf { }
            public enum Mode { A }
        }

        public abstract class Mid : Template
        {
            public abstract override void Hook();
            public sealed override void Run() { }
            public override int Count => 0;
            public override event System.EventHandler? Fired;
            public override int this[int i] => i;
        }

        public class Concrete
        {
            public int Value;
            public virtual void Go() { }
        }
        """;

    [Fact]
    public void AbstractFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.AbstractFilter);
    }

    private static async Task<IReadOnlyList<string>> GetMembersAsync(
        TempWorkspace workspace, string symbolName, bool? abstractFilter, bool? staticFilter = null)
    {
        var operation = new GetSymbolInfoOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = symbolName,
            AbstractFilter = abstractFilter,
            StaticFilter = staticFilter
        });
        Assert.True(result.Success);
        Assert.Equal(result.Data!.Members!.Count, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        return result.Data.Members!;
    }

    private static void AssertMembers(IReadOnlyList<string> members, params string[] expectedNames)
    {
        Assert.Equal(expectedNames.Length, members.Count);
        foreach (var name in expectedNames)
            Assert.Contains(members, m => m.Contains(name, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_OmittedAbstractFilter_ReturnsAllMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        // The abstract indexer never appears in Members (not referenceable by name).
        var members = await GetMembersAsync(workspace, "Template", abstractFilter: null);
        AssertMembers(members, "Run", "Count", "Fired", "Hook", "Execute", "state",
            "InnerBase", "INested", "Helpers", "Leaf", "Mode");
        Assert.DoesNotContain(members, m => m.Contains("this[", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterTrue_KeepsOnlyAbstractClassMembersAndNestedAbstractTypes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        // Nested abstract classes and nested interfaces count as abstract; a static class does not.
        var members = await GetMembersAsync(workspace, "Template", abstractFilter: true);
        AssertMembers(members, "Run", "Count", "Fired", "InnerBase", "INested");
        Assert.DoesNotContain(members, m => m.Contains("Helpers", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("this[", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterFalse_KeepsOnlyNonAbstractMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        var members = await GetMembersAsync(workspace, "Template", abstractFilter: false);
        AssertMembers(members, "Hook", "Execute", "state", "Helpers", "Leaf", "Mode");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilter_InterfaceMembersWithoutBodyAreAbstract()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        // Members without a default body (including static abstract) are abstract; default interface
        // members, static virtual members with a body and constants are not.
        AssertMembers(await GetMembersAsync(workspace, "IShape", abstractFilter: true),
            "Sides", "Area", "Create", "Changed");
        AssertMembers(await GetMembersAsync(workspace, "IShape", abstractFilter: false),
            "Tag", "Describe", "Max");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterCombinedWithStaticFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        AssertMembers(await GetMembersAsync(workspace, "IShape", abstractFilter: true, staticFilter: true),
            "Create");
        AssertMembers(await GetMembersAsync(workspace, "IShape", abstractFilter: false, staticFilter: true),
            "Tag", "Max");
        AssertMembers(await GetMembersAsync(workspace, "IShape", abstractFilter: true, staticFilter: false),
            "Sides", "Area", "Changed");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilter_AbstractOverrideIsAbstractSealedOverrideIsNot()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        AssertMembers(await GetMembersAsync(workspace, "Mid", abstractFilter: true), "Hook");
        AssertMembers(await GetMembersAsync(workspace, "Mid", abstractFilter: false), "Run", "Count", "Fired");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterTrue_ConcreteTypeReturnsEmptyMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);

        Assert.Empty(await GetMembersAsync(workspace, "Concrete", abstractFilter: true));
        AssertMembers(await GetMembersAsync(workspace, "Concrete", abstractFilter: false), "Value", "Go");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterTrue_ConcreteTypeKeepsNestedAbstractTypesAndInterfaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public class Outer
            {
                public int Value;
                public void Go() { }
                public abstract class NestedBase { }
                public abstract record NestedRecord;
                public interface INestedContract { }
                public sealed class NestedLeaf { }
            }
            """);

        // A concrete target is empty for true only when it declares no nested abstract types / interfaces.
        AssertMembers(await GetMembersAsync(workspace, "Outer", abstractFilter: true),
            "NestedBase", "NestedRecord", "INestedContract");
        AssertMembers(await GetMembersAsync(workspace, "Outer", abstractFilter: false),
            "Value", "Go", "NestedLeaf");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterCombinedWithKindFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Template",
            KindFilter = "Method",
            AbstractFilter = true
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Single(members);
        Assert.Contains("Run", members[0], StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterAppliedBeforeMaxResults()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Template",
            AbstractFilter = true,
            MaxResults = 2
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Members!.Count);
        Assert.Equal(5, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilter_AbstractEventIsAbstractFieldLikeEventIsNot()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public abstract class Publisher
            {
                public abstract event System.EventHandler? AbstractRaised;
                public event System.EventHandler? FieldLikeRaised;
            }
            """);

        AssertMembers(await GetMembersAsync(workspace, "Publisher", abstractFilter: true), "AbstractRaised");
        AssertMembers(await GetMembersAsync(workspace, "Publisher", abstractFilter: false), "FieldLikeRaised");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilterTrue_AbstractTargetWithNoAbstractMembersIsStillReturned()
    {
        await using var workspace = await TempWorkspace.CreateAsync("""
            public abstract class Shell
            {
                public void Go() { }
                public int Value;
                public abstract class EmptyNestedBase
                {
                    public void Helper() { }
                }
            }
            """);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        // The filter only affects Members: the abstract target itself is always reported, and with no
        // abstract members its Members list is empty for true.
        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "EmptyNestedBase",
            AbstractFilter = true
        });
        Assert.True(result.Success);
        Assert.Equal("EmptyNestedBase", result.Data!.Name);
        Assert.Empty(result.Data.Members!);
        Assert.Equal(0, result.Data.TotalCount);

        // A nested abstract class with no abstract members still matches true on its containing type.
        AssertMembers(await GetMembersAsync(workspace, "Shell", abstractFilter: true), "EmptyNestedBase");
        AssertMembers(await GetMembersAsync(workspace, "Shell", abstractFilter: false), "Go", "Value");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilter_MetadataTargetJudgesEachMemberOnItsOwnIsAbstract()
    {
        const string source = """
            public class Holder
            {
                public System.IO.Stream? Target;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        async Task<IReadOnlyList<string>> StreamMembersAsync(bool? abstractFilter)
        {
            var result = await operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                Line = 3,
                Column = source.Split('\n')[2].IndexOf("Stream", StringComparison.Ordinal) + 1,
                AbstractFilter = abstractFilter
            });
            Assert.True(result.Success);
            Assert.Equal("Stream", result.Data!.Name);
            return result.Data.Members!;
        }

        // Each metadata member is judged on its own IsAbstract (named members, not counts, so the test
        // does not depend on the reference-assembly version).
        var abstractMembers = await StreamMembersAsync(true);
        Assert.Contains("int Stream.Read(byte[] buffer, int offset, int count)", abstractMembers);
        Assert.Contains("void Stream.Write(byte[] buffer, int offset, int count)", abstractMembers);
        Assert.Contains("bool Stream.CanRead", abstractMembers);
        Assert.DoesNotContain(abstractMembers, m => m.Contains("Stream.CopyTo(", StringComparison.Ordinal));
        Assert.DoesNotContain("int Stream.Read(Span<byte> buffer)", abstractMembers);

        var concreteMembers = await StreamMembersAsync(false);
        Assert.Contains("void Stream.CopyTo(Stream destination)", concreteMembers);
        Assert.Contains("int Stream.Read(Span<byte> buffer)", concreteMembers);
        Assert.Contains("bool Stream.CanTimeout", concreteMembers);
        Assert.DoesNotContain("int Stream.Read(byte[] buffer, int offset, int count)", concreteMembers);
        Assert.DoesNotContain("void Stream.Write(byte[] buffer, int offset, int count)", concreteMembers);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_AbstractFilter_NoEffectOnNonTypeSymbol()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AbstractMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Execute",
            AbstractFilter = true
        });

        Assert.True(result.Success);
        Assert.Null(result.Data!.Members);
        Assert.Null(result.Data.TotalCount);
    }

    #endregion

    #region SealedFilter (#3356)

    private const string SealedMixSource = """
        using System;

        public class Base
        {
            public virtual void Run() { }
            public virtual int Count => 0;
            public virtual event EventHandler? Fired;
            public virtual int this[int i] => i;
            public virtual void Other() { }
        }

        public interface IProbe
        {
            void Ping();
            int this[string key] { get; }
        }

        public class Derived : Base, IProbe
        {
            public Derived() { }
            static Derived() { }
            ~Derived() { }
            public static Derived operator +(Derived a, Derived b) => a;
            public static explicit operator int(Derived d) => 0;
            void IProbe.Ping() { }
            int IProbe.this[string key] => 0;
            public sealed override void Run() { }
            public sealed override int Count => 1;
            public sealed override event EventHandler? Fired;
            public sealed override int this[int i] => i + 1;
            public override void Other() { }
            public void Plain() { }
            protected int state;
            public const int Max = 4;
            public sealed class Leaf { }
            public sealed record LeafRecord;
            public struct Point { }
            public readonly struct RoPoint { }
            public record struct RecPoint;
            public enum Mode { A }
            public delegate void Handler();
            public class Open { }
            public abstract class AbstractInner { }
            public static class Helpers { }
            public interface INested { }
            public record OpenRecord;
        }

        public interface IShape
        {
            void Draw();
            sealed void Describe() { }
            virtual void Hook() { }
        }

        public struct Vec
        {
            public int X;
            public void Move() { }
            public override string ToString() => "";
        }

        public sealed class Final
        {
            public void Go() { }
        }
        """;

    private static async Task<IReadOnlyList<string>> GetSealedMembersAsync(
        TempWorkspace workspace, string symbolName, bool? sealedFilter,
        bool? staticFilter = null, bool? abstractFilter = null)
    {
        var operation = new GetSymbolInfoOperation(workspace.Context);
        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = symbolName,
            SealedFilter = sealedFilter,
            StaticFilter = staticFilter,
            AbstractFilter = abstractFilter
        });
        Assert.True(result.Success);
        Assert.Equal(symbolName, result.Data!.Name);
        Assert.Equal(result.Data.Members!.Count, result.Data.TotalCount);
        Assert.False(result.Data.Truncated);
        return result.Data.Members!;
    }

    [Fact]
    public void GetSymbolInfoParams_SealedFilter_DefaultsToNull()
    {
        var @params = new GetSymbolInfoParams { SourceFile = "/tmp/x.cs", SymbolName = "Foo" };
        Assert.Null(@params.SealedFilter);
    }

    // Derived's exact Members (display strings) per sealedFilter value. The indexer, the operator, the conversion
    // operator, the instance and static constructors, the accessors and the explicit interface implementations
    // (IProbe.Ping and the IProbe indexer) never appear; the finalizer appears as Derived.~Derived() and is unsealed.
    private static readonly string[] SealedDerivedMembers =
    [
        "void Derived.Run()",
        "int Derived.Count",
        "event EventHandler? Derived.Fired",
        "Leaf",
        "LeafRecord",
        "Point",
        "RoPoint",
        "RecPoint",
        "Mode",
        "Handler"
    ];

    private static readonly string[] UnsealedDerivedMembers =
    [
        "Derived.~Derived()",
        "void Derived.Other()",
        "void Derived.Plain()",
        "int Derived.state",
        "int Derived.Max",
        "Open",
        "AbstractInner",
        "Helpers",
        "INested",
        "OpenRecord"
    ];

    private static void AssertExactMembers(IReadOnlyList<string> members, IEnumerable<string> expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), members.Order(StringComparer.Ordinal));

    [SkippableFact]
    public async Task GetSymbolInfo_OmittedSealedFilter_ReturnsAllMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        var members = await GetSealedMembersAsync(workspace, "Derived", sealedFilter: null);
        AssertExactMembers(members, SealedDerivedMembers.Concat(UnsealedDerivedMembers));
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilterTrue_KeepsSealedOverridesAndSealedNestedTypes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        var members = await GetSealedMembersAsync(workspace, "Derived", sealedFilter: true);
        AssertExactMembers(members, SealedDerivedMembers);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilterFalse_KeepsUnsealedMembersAndOpenNestedTypes()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        var members = await GetSealedMembersAsync(workspace, "Derived", sealedFilter: false);
        AssertExactMembers(members, UnsealedDerivedMembers);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_NeverReturnsIndexersOperatorsConstructorsOrAccessors()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        // Derived declares a sealed override indexer, an operator +, an explicit conversion operator, an explicit
        // instance constructor, a static constructor, IProbe.Ping and an explicit IProbe indexer; none is a Member
        // for any sealedFilter value, so each list is exactly the named members below.
        AssertExactMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: null),
            SealedDerivedMembers.Concat(UnsealedDerivedMembers));
        AssertExactMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: true), SealedDerivedMembers);
        AssertExactMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: false), UnsealedDerivedMembers);
    }

    [SkippableTheory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GetSymbolInfo_SealedFilter_FinalizerIsReturnedAndUnsealed(bool? sealedFilter, bool expectFinalizer)
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        var members = await GetSealedMembersAsync(workspace, "Derived", sealedFilter: sealedFilter);
        if (expectFinalizer)
            Assert.Contains("Derived.~Derived()", members);
        else
            Assert.DoesNotContain("Derived.~Derived()", members);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_InterfaceMembersAreNeverSealed()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        Assert.Empty(await GetSealedMembersAsync(workspace, "IShape", sealedFilter: true));
        AssertMembers(await GetSealedMembersAsync(workspace, "IShape", sealedFilter: false),
            "Draw", "Describe", "Hook");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_StructAndSealedClassTargetsAreAlwaysReturned()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        Assert.Empty(await GetSealedMembersAsync(workspace, "Vec", sealedFilter: true));
        AssertMembers(await GetSealedMembersAsync(workspace, "Vec", sealedFilter: false), "X", "Move", "ToString");
        Assert.Empty(await GetSealedMembersAsync(workspace, "Final", sealedFilter: true));
        AssertMembers(await GetSealedMembersAsync(workspace, "Final", sealedFilter: false), "Go");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_CombinesWithStaticAndAbstractFilters()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);

        AssertMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: false, staticFilter: true),
            "Max", "Helpers");
        Assert.Empty(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: true, staticFilter: true));
        // Nested structs, enums and delegates are sealed but not static.
        AssertExactMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: true, staticFilter: false),
            SealedDerivedMembers);
        Assert.Empty(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: true, abstractFilter: true));
        AssertMembers(await GetSealedMembersAsync(workspace, "Derived", sealedFilter: false, abstractFilter: true),
            "AbstractInner", "INested");
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilterCombinedWithKindFilter_RequiresBoth()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Derived",
            KindFilter = "Method",
            SealedFilter = true
        });

        Assert.True(result.Success);
        var members = result.Data!.Members!;
        Assert.Single(members);
        Assert.Contains("Run", members[0], StringComparison.Ordinal);
        Assert.Equal(1, result.Data.TotalCount);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilterAppliedBeforeMaxResults()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Derived",
            SealedFilter = true,
            MaxResults = 3
        });

        Assert.True(result.Success);
        Assert.Equal(3, result.Data!.Members!.Count);
        Assert.Equal(10, result.Data.TotalCount);
        Assert.True(result.Data.Truncated);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_MetadataTargetJudgesEachMemberOnItsOwnIsSealed()
    {
        const string source = """
            public class Holder
            {
                public System.Collections.Generic.List<int>? Target;
            }
            """;
        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        async Task<IReadOnlyList<string>> ListMembersAsync(bool? sealedFilter)
        {
            var result = await operation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = workspace.SourcePath,
                Line = 3,
                Column = source.Split('\n')[2].IndexOf("List", StringComparison.Ordinal) + 1,
                SealedFilter = sealedFilter
            });
            Assert.True(result.Success);
            Assert.Equal("List", result.Data!.Name);
            return result.Data.Members!;
        }

        // Named members, not counts, so the test does not depend on the reference-assembly version.
        var sealedMembers = await ListMembersAsync(true);
        Assert.Contains("Enumerator", sealedMembers);
        Assert.DoesNotContain(sealedMembers, m => m.Contains(".Add(", StringComparison.Ordinal));

        var unsealedMembers = await ListMembersAsync(false);
        Assert.Contains(unsealedMembers, m => m.Contains(".Add(", StringComparison.Ordinal));
        Assert.DoesNotContain("Enumerator", unsealedMembers);
    }

    [SkippableFact]
    public async Task GetSymbolInfo_SealedFilter_NoEffectOnNonTypeSymbol()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SealedMixSource);
        var operation = new GetSymbolInfoOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GetSymbolInfoParams
        {
            SourceFile = workspace.SourcePath,
            SymbolName = "Plain",
            SealedFilter = true
        });

        Assert.True(result.Success);
        Assert.Null(result.Data!.Members);
        Assert.Null(result.Data.TotalCount);

        const string namespaceSource = """
            namespace App.Sealing
            {
                public sealed class Leaf { }
            }
            """;
        await using var namespaceWorkspace = await TempWorkspace.CreateAsync(namespaceSource);
        var namespaceOperation = new GetSymbolInfoOperation(namespaceWorkspace.Context);
        foreach (var filter in new bool?[] { null, true, false })
        {
            var namespaceResult = await namespaceOperation.ExecuteAsync(new GetSymbolInfoParams
            {
                SourceFile = namespaceWorkspace.SourcePath,
                Line = 1,
                Column = namespaceSource.IndexOf("Sealing", StringComparison.Ordinal) + 1,
                SealedFilter = filter
            });

            Assert.True(namespaceResult.Success);
            Assert.Equal("Sealing", namespaceResult.Data!.Name);
            Assert.Equal(RoslynMcp.Contracts.Enums.SymbolKind.Namespace, namespaceResult.Data.Kind);
            Assert.Null(namespaceResult.Data.Members);
            Assert.Null(namespaceResult.Data.TotalCount);
        }
    }

    #endregion
}

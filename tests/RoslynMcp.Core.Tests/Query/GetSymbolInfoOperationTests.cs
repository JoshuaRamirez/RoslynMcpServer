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
        Assert.Equal(10, result.Data!.TotalCount);
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
        Assert.Equal(4, members.Count);
        Assert.Equal(4, result.Data.TotalCount);
        Assert.Contains(members, m => m.Contains("Instance", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Label", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Run", StringComparison.Ordinal));
        Assert.Contains(members, m => m.Contains("Nested", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Limit", StringComparison.Ordinal));
        Assert.DoesNotContain(members, m => m.Contains("Create", StringComparison.Ordinal));
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

    #endregion
}

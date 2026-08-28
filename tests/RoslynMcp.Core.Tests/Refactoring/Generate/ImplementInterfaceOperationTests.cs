using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Operation-level tests for <see cref="ImplementInterfaceOperation"/>, including
/// indexer stubs via <c>SyntaxGenerationHelper.CreateIndexerStub</c>.
/// </summary>
public class ImplementInterfaceOperationTests
{
    private const string MixedInterfaceSource = """
        namespace TestApp;

        public interface IWidget
        {
            void DoWork();
            int Count { get; set; }
            string this[int i] { get; set; }
            event EventHandler Changed;
        }

        public class Widget : IWidget
        {
        }
        """;

    private const string IndexerOnlySource = """
        namespace TestApp;

        public interface ILookup
        {
            string this[int i] { get; set; }
        }

        public class Lookup : ILookup
        {
        }
        """;

    #region Defaults

    [Fact]
    public void ThrowNotImplemented_DefaultsToTrue()
    {
        var @params = new ImplementInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        };

        Assert.True(@params.ThrowNotImplemented);
        Assert.False(@params.ExplicitImplementation);
        Assert.False(@params.ReplaceExisting);
        Assert.False(@params.Preview);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new ImplementInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        };

        Assert.False(@params.ReplaceExisting);
    }

    #endregion

    #region Happy Path / Regressions

    [SkippableFact]
    public async Task ImplementInterface_Method_AddsStub()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("void DoWork()", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_OrdinaryProperty_EmitsPropertyDeclaration()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Count { get; set; }
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Widget", "Count");
        Assert.NotNull(property);
        Assert.Empty(FindIndexers(updated, "Widget"));
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Contains("throw new NotImplementedException()", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_Event_AddsStub()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                event EventHandler Changed;
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = FindEvent(updated, "Widget", "Changed");
        Assert.NotNull(evt);
        Assert.Contains("add", updated);
        Assert.Contains("remove", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ExplicitImplementation_Method()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ExplicitImplementation = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var method = FindMethod(updated, "Widget", "DoWork");
        Assert.NotNull(method);
        Assert.NotNull(method!.ExplicitInterfaceSpecifier);
        Assert.Contains("IWidget", method.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.Contains("throw new NotImplementedException()", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ThrowNotImplementedFalse_Method_DefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Size();
            }

            public class Widget : IWidget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("int Size()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
    }

    #endregion

    #region Indexers

    [SkippableFact]
    public async Task ImplementInterface_Indexer_EmitsIndexerDeclarationNotPropertyNamedThis()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.DoesNotContain(FindType(updated, "Lookup").Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ThrowNotImplementedTrue_UsesThrowBodies()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ThrowNotImplemented = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        var getter = ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration);
        var setter = ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration);
        Assert.Contains("throw new NotImplementedException()", getter);
        Assert.Contains("throw new NotImplementedException()", setter);
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_DefaultThrowNotImplemented_UsesThrowBodies()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ThrowNotImplementedFalse_DefaultReturnGetterEmptySetter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_Implicit_IsPublicWithoutOverride_AndCompiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.Contains(indexer.Modifiers, t => t.IsKind(SyntaxKind.PublicKeyword));
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("public string this[int i]", updated);
        Assert.DoesNotContain("override", ExtractMemberText(indexer));
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImplementInterface_RefIndexer_KeepsRef_AndThrowsEvenWhenThrowNotImplementedFalse(
        bool throwNotImplemented)
    {
        const string source = """
            namespace TestApp;

            public interface ICell
            {
                ref int this[int i] { get; }
            }

            public class Cell : ICell
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Cell",
            InterfaceName = "ICell",
            ThrowNotImplemented = throwNotImplemented
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Cell"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("public ref int this[int i]", updated);
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImplementInterface_RefReadonlyIndexer_KeepsRefReadonly_AndThrowsEvenWhenThrowNotImplementedFalse(
        bool throwNotImplemented)
    {
        const string source = """
            namespace TestApp;

            public interface IOrigin
            {
                ref readonly int this[int i] { get; }
            }

            public class Origin : IOrigin
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Origin",
            InterfaceName = "IOrigin",
            ThrowNotImplemented = throwNotImplemented
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Origin"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.True(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("public ref readonly int this[int i]", updated);
        Assert.DoesNotContain(indexer.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ExplicitImplementation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ExplicitImplementation = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.NotNull(indexer.ExplicitInterfaceSpecifier);
        Assert.Contains("ILookup", indexer.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.Contains("this[int i]", updated);
        Assert.Empty(indexer.Modifiers);
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_ExplicitImplementation_ThrowNotImplementedFalse()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ExplicitImplementation = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.NotNull(indexer.ExplicitInterfaceSpecifier);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain("return", ExtractAccessor(indexer, SyntaxKind.SetAccessorDeclaration));
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task ImplementInterface_MembersFilter_IndexerAliases_ImplementsOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { memberName }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Single(FindIndexers(updated, "Widget"));
        Assert.Contains("this[int i]", updated);
        Assert.Null(FindMethod(updated, "Widget", "DoWork"));
        Assert.Null(FindProperty(updated, "Widget", "Count"));
        Assert.Null(FindEvent(updated, "Widget", "Changed"));
    }

    [SkippableFact]
    public async Task ImplementInterface_MembersFilter_Property_DoesNotImplementIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { "Count" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Empty(FindIndexers(updated, "Widget"));
        Assert.Null(FindMethod(updated, "Widget", "DoWork"));
        Assert.Null(FindEvent(updated, "Widget", "Changed"));
    }

    [SkippableFact]
    public async Task ImplementInterface_Indexer_Preview_WritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("this[]", result.PendingChanges[0].Description);
        Assert.Contains("this[int i]", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("this[]", result.PendingChanges[0].AfterSnippet?.Replace("this[int i]", "", StringComparison.Ordinal) ?? "");
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_MixedMembers_MethodPropertyIndexerEvent()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Single(FindIndexers(updated, "Widget"));
        Assert.NotNull(FindEvent(updated, "Widget", "Changed"));
        Assert.Contains("this[int i]", updated);
    }

    #endregion

    #region replaceExisting false / omitted

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingOmitted_AlreadyImplemented_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                InterfaceName = "IWidget"
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-body", before);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingFalse_AlreadyImplemented_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                InterfaceName = "IWidget",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting true

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ReplacesMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var method = FindMethod(updated, "Widget", "DoWork");
        Assert.NotNull(method);
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.Equal(1, CountOccurrences(updated, "void DoWork("));
        Assert.DoesNotContain("void get_", updated);
        Assert.DoesNotContain("void set_", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ReplacesProperty()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Count { get; set; }
            }

            public class Widget : IWidget
            {
                public int Count { get; set; } = 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "Widget", "Count");
        Assert.NotNull(property);
        Assert.DoesNotContain("= 42", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.Empty(FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text is "get_Count" or "set_Count"));
        Assert.DoesNotContain("get_Count", updated);
        Assert.DoesNotContain("set_Count", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ReplacesIndexer()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; set; }
            }

            public class Lookup : ILookup
            {
                public string this[int i]
                {
                    get => "old";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "Lookup"));
        Assert.DoesNotContain("old", updated);
        Assert.Contains("throw new NotImplementedException()", ExtractAccessor(indexer, SyntaxKind.GetAccessorDeclaration));
        Assert.Empty(FindType(updated, "Lookup").Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text is "get_Item" or "set_Item" or "get_this" or "set_this"));
        Assert.DoesNotContain("get_Item", updated);
        Assert.DoesNotContain("set_Item", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ReplacesEvent()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                event EventHandler Changed;
            }

            public class Widget : IWidget
            {
                public event EventHandler Changed { add { } remove { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.NotNull(FindEvent(updated, "Widget", "Changed"));
        Assert.Equal(1, CountOccurrences(updated, "event EventHandler Changed"));
        Assert.Empty(FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text.StartsWith("add_", StringComparison.Ordinal)
                || m.Identifier.Text.StartsWith("remove_", StringComparison.Ordinal)));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_OmittedMembers_ReplacesExisting_AndAddsMissing()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
                int Count { get; set; }
            }

            public class Widget : IWidget
            {
                public void DoWork() { /* old-body */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-body", updated);
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Equal(1, CountOccurrences(updated, "void DoWork("));
        Assert.Contains("throw new NotImplementedException()", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_NamedMembers_OnlyReplacesThose()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
                int Count { get; set; }
            }

            public class Widget : IWidget
            {
                public void DoWork() { /* old-body */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { "DoWork" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-body", updated);
        Assert.NotNull(FindMethod(updated, "Widget", "DoWork"));
        Assert.Null(FindProperty(updated, "Widget", "Count"));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ThrowNotImplementedFalse_ReplacesWithDefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Size();
            }

            public class Widget : IWidget
            {
                public int Size() => 99;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ThrowNotImplemented = false,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("=> 99", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ExplicitImplementation_ReplacesExplicitForm()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
                void IWidget.DoWork() { /* old-explicit */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ExplicitImplementation = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var method = FindMethod(updated, "Widget", "DoWork");
        Assert.NotNull(method);
        Assert.NotNull(method!.ExplicitInterfaceSpecifier);
        Assert.Contains("IWidget", method.ExplicitInterfaceSpecifier!.Name.ToString());
        Assert.DoesNotContain("old-explicit", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.Equal(1, CountOccurrences(updated, "DoWork("));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_DoesNotEmitAccessorMethods()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                int Count { get; set; }
                string this[int i] { get; set; }
                event EventHandler Changed;
            }

            public class Widget : IWidget
            {
                public int Count { get; set; }
                public string this[int i] { get => ""; set { } }
                public event EventHandler Changed { add { } remove { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var methods = FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>().ToList();
        Assert.Empty(methods);
        Assert.DoesNotContain("get_Count", updated);
        Assert.DoesNotContain("set_Count", updated);
        Assert.DoesNotContain("get_Item", updated);
        Assert.DoesNotContain("set_Item", updated);
        Assert.DoesNotContain("add_Changed", updated);
        Assert.DoesNotContain("remove_Changed", updated);
        Assert.NotNull(FindProperty(updated, "Widget", "Count"));
        Assert.Single(FindIndexers(updated, "Widget"));
        Assert.NotNull(FindEvent(updated, "Widget", "Changed"));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_PartialOtherFile_RemovesThere_InsertsOnTarget()
    {
        const string typePart = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public partial class Widget : IWidget
            {
            }
            """;

        const string implPart = """
            namespace TestApp;

            public partial class Widget
            {
                public void DoWork() { /* old-partial */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", typePart),
            ("Widget.Impl.cs", implPart));
        var otherPath = workspace.PathFor("Widget.Impl.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        Assert.NotNull(FindMethod(selected, "Widget", "DoWork"));
        Assert.Contains("throw new NotImplementedException()", selected);
        Assert.DoesNotContain("old-partial", selected);
        Assert.DoesNotContain("void DoWork(", other);
        Assert.DoesNotContain("old-partial", other);
        Assert.Equal(1, CountOccurrences(selected, "void DoWork("));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_Preview_WritesNothing_AndMentionsReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("replace existing", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DoWork", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing interface members", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("throw new NotImplementedException()", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("old-body", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string typePart = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public partial class Widget : IWidget
            {
            }
            """;

        const string implPart = """
            namespace TestApp;

            public partial class Widget
            {
                public void DoWork() { /* old-partial */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", typePart),
            ("Widget.Impl.cs", implPart));
        var otherPath = workspace.PathFor("Widget.Impl.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("replace existing", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        var otherChange = result.PendingChanges[1];
        Assert.Equal(otherPath, otherChange.File);
        Assert.Equal(ChangeKind.Modify, otherChange.ChangeType);
        Assert.Contains("Remove existing method 'DoWork'", otherChange.Description);
        Assert.Contains("old-partial", otherChange.BeforeSnippet);
        Assert.Equal("// method removed", otherChange.AfterSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_AmbiguousSameName_NameCollision_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public interface IHandler
            {
                void Handle(int x);
                void Handle(string s);
                void Handle(object o);
            }

            public class Handler : IHandler
            {
                public void Handle(int x) { }

                public void Handle(string s) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Handler",
                InterfaceName = "IHandler",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal("3003", ex.ErrorCode);
        Assert.Contains("Handle", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_UnknownMemberFilter_ThrowsAlreadyImplemented()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                InterfaceName = "IWidget",
                Members = new[] { "DoesNotExist" },
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_IfDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            #if DEBUG
                public void DoWork() { /* old-if */ }
            #endif

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#if DEBUG", updated);
        Assert.Contains("#endif", updated);
        Assert.Contains("void DoWork()", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old-if", updated);
        Assert.Equal(updated.Split("#if ").Length - 1, updated.Split("#endif").Length - 1);
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_RegionDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget
            {
            #region Work
                public void DoWork() { /* old-region */ }
            #endregion

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#region Work", updated);
        Assert.Contains("#endregion", updated);
        Assert.Contains("void DoWork()", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old-region", updated);
        Assert.Equal(updated.Split("#region ").Length - 1, updated.Split("#endregion").Length - 1);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task ImplementInterface_TypeNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                InterfaceName = "ILookup"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_InterfaceNotFound_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(IndexerOnlySource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "IMissing"
            }));

        Assert.Equal(ErrorCodes.InterfaceNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_AlreadyImplemented_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface ILookup
            {
                string this[int i] { get; set; }
            }

            public class Lookup : ILookup
            {
                public string this[int i]
                {
                    get => "";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "ILookup"
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_UnknownMemberFilter_ThrowsAlreadyImplemented()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedInterfaceSource);
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                InterfaceName = "IWidget",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberAlreadyImplemented, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private const string AlreadyImplementedMethodSource = """
        namespace TestApp;

        public interface IWidget
        {
            void DoWork();
        }

        public class Widget : IWidget
        {
            public void DoWork() { /* old-body */ }
        }
        """;

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpImplementInterfaceMissing.cs");

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static TypeDeclarationSyntax FindType(string source, string typeName)
    {
        var type = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.Text == typeName);
        Assert.True(type != null, $"Generated source did not contain type '{typeName}':\n{source}");
        return type!;
    }

    private static IReadOnlyList<IndexerDeclarationSyntax> FindIndexers(string source, string typeName) =>
        FindType(source, typeName).Members.OfType<IndexerDeclarationSyntax>().ToList();

    private static PropertyDeclarationSyntax? FindProperty(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == name);

    private static MethodDeclarationSyntax? FindMethod(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == name);

    private static EventDeclarationSyntax? FindEvent(string source, string typeName, string name) =>
        FindType(source, typeName).Members.OfType<EventDeclarationSyntax>()
            .FirstOrDefault(e => e.Identifier.Text == name);

    private static string ExtractAccessor(IndexerDeclarationSyntax indexer, SyntaxKind kind)
    {
        var accessor = indexer.AccessorList?.Accessors.FirstOrDefault(a => a.Kind() == kind);
        Assert.NotNull(accessor);
        return accessor!.ToFullString();
    }

    private static string ExtractMemberText(MemberDeclarationSyntax member) =>
        NormalizeNewlines(member.NormalizeWhitespace().ToFullString());

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ImplementInterfaceCompileTest",
                new[]
                {
                    CSharpSyntaxTree.ParseText("global using System;"),
                    CSharpSyntaxTree.ParseText(source)
                },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated implement_interface stubs did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpImplementInterface_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Types.cs");

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                if (context.GetDocumentByPath(sourcePath) == null)
                {
                    context.Dispose();
                    throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = sourcePath,
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

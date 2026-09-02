using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
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
/// optional <c>line</c> / <c>column</c> disambiguation and indexer stubs via
/// <c>SyntaxGenerationHelper.CreateIndexerStub</c>.
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
        Assert.False(@params.AllFiles);
        Assert.Null(@params.Line);
        Assert.Null(@params.Column);
    }

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new ImplementInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new ImplementInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
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

    #region P0 optional line disambiguation

    private const string NestedSameNameWidgetSource = """
        namespace TestApp;

        public interface IWidget
        {
            void DoWork();
        }

        public class Widget : IWidget // outer-widget
        {
            public class Widget : IWidget // nested-widget
            {
            }
        }
        """;

    [SkippableFact]
    public async Task ImplementInterface_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
        Assert.False(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "DoWork"));
        Assert.True(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "outer-widget"),
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
        Assert.False(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            InterfaceName = "IWidget",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Implement IWidget members:", result.PendingChanges[0].Description);
        Assert.Contains("DoWork", result.PendingChanges[0].Description);
        Assert.Contains("void DoWork()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "nested-widget"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "outer-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class
                Widget : IWidget // split-widget
            {
                public class Widget : IWidget // nested-widget
                {
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = ImplementInterfaceOperation.FindTypeDeclaration(root, "Widget", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(root, "Widget", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(ImplementInterfaceOperation.SpanCoversLine(span, 1));
        Assert.True(ImplementInterfaceOperation.SpanCoversLine(span, 2));
        Assert.False(ImplementInterfaceOperation.SpanCoversLine(span, 3));
        Assert.False(ImplementInterfaceOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task ImplementInterface_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
    {
        const string source = """
            public interface IWidget
            {
                void DoWork();
            }

            namespace Other
            {
                public class Widget : IWidget
                {
                }
            }

            namespace TestApp
            {
                public partial class Widget : IWidget
                {
                    public void DoWork() { /* old-body */ }
                }

                public partial class Widget // later-partial
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(source, "later-partial"),
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasMethod(types[0], "DoWork"));
        Assert.False(TypeHasMethod(types[1], "DoWork"));
        Assert.True(TypeHasMethod(types[2], "DoWork"));
        Assert.DoesNotContain("old-body", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(await File.ReadAllTextAsync(workspace.SourcePath), "public void DoWork("));
    }

    [SkippableFact]
    public async Task ImplementInterface_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public interface IAlpha
            {
                void AlphaWork();
            }

            public interface IBeta
            {
                void BetaWork();
            }

            public class Alpha : IAlpha
            {
                public void AlphaWork() { /* old-alpha */ }
            }

            public class Beta : IBeta
            {
                public void BetaWork() { /* old-beta */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            InterfaceName = "IAlpha",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta",
            InterfaceName = "IBeta",
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var alpha = FindType(updated, "Alpha");
        var beta = FindType(updated, "Beta");
        Assert.True(TypeHasMethod((ClassDeclarationSyntax)alpha, "AlphaWork"));
        Assert.False(TypeHasMethod((ClassDeclarationSyntax)alpha, "BetaWork"));
        Assert.True(TypeHasMethod((ClassDeclarationSyntax)beta, "BetaWork"));
        Assert.False(TypeHasMethod((ClassDeclarationSyntax)beta, "AlphaWork"));
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", alpha.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", beta.ToFullString(), StringComparison.Ordinal);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedWidgetSource = """
        namespace TestApp;

        public interface IWidget
        {
            void DoWork();
        }

        public class Widget : IWidget { public class Widget : IWidget { } }
        """;

    [SkippableFact]
    public async Task ImplementInterface_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
        Assert.False(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "DoWork"));
        Assert.True(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : IWidget { public class");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { }");

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column,
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "DoWork"));
        Assert.True(TypeHasMethod(types[1], "DoWork"));
    }

    [SkippableFact]
    public async Task ImplementInterface_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : IWidget { public class");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { public class");

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column,
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
        Assert.False(TypeHasMethod(types[1], "DoWork"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(root, "Widget", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : IWidget { public class");
        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { }"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : IWidget { public class");
        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { public class"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { }");
        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class
                Widget : IWidget // split-widget
            {
                public class Widget : IWidget // nested-widget
                {
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = ImplementInterfaceOperation.FindTypeDeclaration(
            root, "Widget", identifierLine, ColumnOf(source, "Widget : IWidget // split-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [SkippableFact]
    public async Task ImplementInterface_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class
                Widget : IWidget // split-widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Widget");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget : IWidget // split-widget"),
            InterfaceName = "IWidget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementInterfaceOperation.FindTypeDeclaration(root, "Widget", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task ImplementInterface_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = 1,
                Column = 1,
                InterfaceName = "IWidget"
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementInterface_Column_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : IWidget { public class");

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineNestedWidgetSource, "Widget : IWidget { }"),
            InterfaceName = "IWidget",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Implement IWidget members:", result.PendingChanges[0].Description);
        Assert.Contains("DoWork", result.PendingChanges[0].Description);
        Assert.Contains("void DoWork()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void SpanCoversColumn_TreatsEndAsExclusive()
    {
        const string source = "class Outer { class Nested { } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var nested = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == "Nested");
        var span = nested.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var startCol = span.StartLinePosition.Character + 1;
        var endCol = span.EndLinePosition.Character + 1;

        Assert.True(ImplementInterfaceOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ImplementInterfaceOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ImplementInterfaceOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ImplementInterfaceOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ImplementInterface_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void DoWork();
            }

            public class Widget : IWidget { public void DoWork() { /* old-outer */ } public class Widget : IWidget { public void DoWork() { /* old-nested */ } } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var line = FindLine(source, "public class Widget : IWidget { public void DoWork()");

        var first = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(source, "Widget : IWidget { public void DoWork() { /* old-outer */ }"),
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(afterFirst, "old-nested"),
            Column = ColumnOf(afterFirst, "Widget : IWidget { public void DoWork() { /* old-nested */ }"),
            InterfaceName = "IWidget",
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "DoWork"));
        Assert.True(TypeHasMethod(types[1], "DoWork"));
        Assert.DoesNotContain("old-outer", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-nested", types[1].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new NotImplementedException()", types[1].ToFullString(), StringComparison.Ordinal);
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
        Assert.Equal(1, CountOccurrences(ExtractMemberText(FindType(updated, "Widget")), "void DoWork("));
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
        Assert.DoesNotContain(
            FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.Text is "get_Count" or "set_Count");
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
        Assert.DoesNotContain(
            FindType(updated, "Lookup").Members.OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.Text is "get_Item" or "set_Item" or "get_this" or "set_this");
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
        Assert.Equal(1, CountOccurrences(ExtractMemberText(FindType(updated, "Widget")), "event EventHandler Changed"));
        Assert.DoesNotContain(
            FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>(),
            m => m.Identifier.Text.StartsWith("add_", StringComparison.Ordinal)
                || m.Identifier.Text.StartsWith("remove_", StringComparison.Ordinal));
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
        Assert.Equal(1, CountOccurrences(ExtractMemberText(FindType(updated, "Widget")), "void DoWork("));
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
        Assert.Equal(1, CountOccurrences(ExtractMemberText(FindType(updated, "Widget")), "DoWork("));
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
        Assert.Equal(1, CountOccurrences(ExtractMemberText(FindType(selected, "Widget")), "void DoWork("));
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

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_ExplicitSameSignature_DoesNotRemoveOtherInterface()
    {
        const string source = """
            namespace TestApp;

            public interface IA
            {
                void M();
            }

            public interface IB
            {
                void M();
            }

            public class Widget : IA, IB
            {
                void IA.M() { /* old-ia */ }
                void IB.M() { /* old-ib */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IA",
            ExplicitImplementation = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var methods = FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "M")
            .ToList();
        Assert.Equal(2, methods.Count);
        var ia = Assert.Single(methods, m => m.ExplicitInterfaceSpecifier?.Name.ToString().Contains("IA") == true);
        var ib = Assert.Single(methods, m => m.ExplicitInterfaceSpecifier?.Name.ToString().Contains("IB") == true);
        Assert.DoesNotContain("old-ia", updated);
        Assert.Contains("old-ib", updated);
        Assert.Contains("throw new NotImplementedException()", ExtractMemberText(ia));
        Assert.Contains("old-ib", ExtractMemberText(ib));
        Assert.Equal(1, methods.Count(m => m.ExplicitInterfaceSpecifier?.Name.ToString().Contains("IA") == true));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_GenericListOfT_ReplacesNotDuplicates()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                void M<T>(System.Collections.Generic.List<T> value);
            }

            public class Widget : IWidget
            {
                public void M<T>(System.Collections.Generic.List<T> value) { /* old-generic */ }
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
        var methods = FindType(updated, "Widget").Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == "M")
            .ToList();
        Assert.Single(methods);
        Assert.DoesNotContain("old-generic", updated);
        Assert.Contains("throw new NotImplementedException()", updated);
        Assert.Contains("List<T>", ExtractMemberText(methods[0]));
    }

    [SkippableFact]
    public async Task ImplementInterface_ReplaceExistingTrue_MultiVariableEventField_LeavesUnrelated()
    {
        const string source = """
            namespace TestApp;

            public interface IWidget
            {
                event System.Action Changed;
            }

            public class Widget : IWidget
            {
                public event System.Action Changed, Unrelated;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            InterfaceName = "IWidget",
            Members = new[] { "Changed" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var widget = FindType(updated, "Widget");
        Assert.NotNull(FindEvent(updated, "Widget", "Changed"));
        var unrelated = widget.Members.OfType<EventFieldDeclarationSyntax>()
            .SelectMany(e => e.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.Text == "Unrelated");
        Assert.NotNull(unrelated);
        Assert.DoesNotContain(
            widget.Members.OfType<EventFieldDeclarationSyntax>()
                .SelectMany(e => e.Declaration.Variables),
            v => v.Identifier.Text == "Changed");
        Assert.Equal(1, widget.Members.OfType<EventDeclarationSyntax>().Count(e => e.Identifier.Text == "Changed"));
        Assert.DoesNotContain("Changed, Unrelated", updated);
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

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public interface IFileA
        {
            void WorkA();
        }

        public class FileA : IFileA
        {
        }

        public static class StaticSkip
        {
        }

        public interface ISkip
        {
            void Skip();
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public interface IFileB
        {
            int Value { get; }
        }

        public class FileB : IFileB
        {
        }
        """;

    private const string IneligibleFileC = """
        namespace TestApp;

        public static class Limits
        {
        }

        public interface IWidget
        {
            void Draw();
        }

        public struct Point
        {
        }

        public class AlreadyImplemented : IWidget
        {
            public void Draw() { }
        }

        public class Empty
        {
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public interface IWork
        {
            void Work();
        }

        public class Eligible : IWork
        {
        }

        public static class StaticSkip
        {
        }

        public interface ISkip
        {
            void Skip();
        }

        public struct PointSkip
        {
        }

        public class Empty
        {
        }

        public interface IOuter
        {
            void OuterWork();
        }

        public class Outer : IOuter
        {
            public interface INested
            {
                int NestedWork();
            }

            public class Nested : INested
            {
            }
        }
        """;

    private const string UndeclaredInterfaceSource = """
        namespace TestApp;

        public interface IKnown
        {
            void Known();
        }

        public interface IUndeclared
        {
            void HuntMe();
        }

        public class Eligible : IKnown
        {
        }

        public class NoInterface
        {
        }
        """;

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = false,
                TypeName = "Widget",
                InterfaceName = "IWidget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                InterfaceName = "IWidget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutInterfaceName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("interfaceName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileTypeNameOrInterfaceName_DoesNotThrow()
    {
        ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithInterfaceName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                InterfaceName = "IWidget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("interfaceName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                Members = new[] { "Work" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("members", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithEmptyMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                Members = Array.Empty<string>()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("members", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TypeWalkKey_IncludesProjectIdentity()
    {
        var projectA = ProjectId.CreateNewId();
        var projectB = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Widget";

        var keyA = ImplementInterfaceOperation.TypeWalkKey(projectA, fqn);
        var keyB = ImplementInterfaceOperation.TypeWalkKey(projectB, fqn);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(keyA, ImplementInterfaceOperation.TypeWalkKey(projectA, fqn));
        Assert.NotEqual(keyA, ImplementInterfaceOperation.TypeWalkKey(projectA, "global::TestApp.Other"));
    }

    [Fact]
    public void TypeWalkKey_FileLocalIdentity_DistinguishesSameFqn()
    {
        var project = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Worker";

        var ordinary = ImplementInterfaceOperation.TypeWalkKey(project, fqn);
        var fileA = ImplementInterfaceOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs");
        var fileB = ImplementInterfaceOperation.TypeWalkKey(project, fqn, "/tmp/FileB.cs");

        Assert.NotEqual(ordinary, fileA);
        Assert.NotEqual(ordinary, fileB);
        Assert.NotEqual(fileA, fileB);
        Assert.Equal(fileA, ImplementInterfaceOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs"));
        Assert.Equal(ordinary, ImplementInterfaceOperation.TypeWalkKey(project, fqn));
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementInterfaceOperation.Validate(new ImplementInterfaceParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Implement interface members", ImplementInterfaceOperation.BuildAllFilesDescription(1));
        Assert.Equal("Implement interface members on 2 types", ImplementInterfaceOperation.BuildAllFilesDescription(2));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesFalse_ImplementsOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("FileB.cs"));
        var beforeC = await File.ReadAllTextAsync(workspace.PathFor("FileC.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.PathFor("FileA.cs"),
            AllFiles = false,
            TypeName = "FileA",
            InterfaceName = "IFileA"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        Assert.Contains("public void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Skip(", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.PathFor("FileC.cs")));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileA.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_OmittedAllFiles_KeepsSingleSiteImplement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "FileA",
            InterfaceName = "IFileA"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public void WorkA()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Skip(", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_ImplementsEligibleTypesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.PathFor("FileC.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Contains("public void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Skip(", updatedA, StringComparison.Ordinal);
        Assert.Contains("public int Value", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.PathFor("FileC.cs")));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileA.cs")));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileB.cs")));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileC.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithoutSourceFileTypeNameOrInterfaceName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = false,
                TypeName = "FileA",
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesFalse_WithoutTypeName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath,
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesFalse_WithoutInterfaceName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("interfaceName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithInterfaceName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = true,
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("interfaceName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithMembers_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = true,
                Members = new[] { "WorkA" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("members", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementInterfaceParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementInterface_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.PathFor("FileA.cs"));
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("FileB.cs"));
        var beforeC = await File.ReadAllTextAsync(workspace.PathFor("FileC.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.PathFor("FileA.cs")));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.PathFor("FileB.cs")));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.PathFor("FileC.cs")));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Implement", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("WorkA", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("Value", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.PathFor("FileC.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("Limits", "Limits2", StringComparison.Ordinal)
                .Replace("IWidget", "IWidget2", StringComparison.Ordinal)
                .Replace("Point", "Point2", StringComparison.Ordinal)
                .Replace("AlreadyImplemented", "AlreadyImplemented2", StringComparison.Ordinal)
                .Replace("Empty", "Empty2", StringComparison.Ordinal)));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.PathFor("FileC.cs"));
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("FileC2.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.PathFor("FileC.cs")));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("FileC2.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_SkipsNoInterfacesAlreadyImplementedAndIneligible()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("Mixed.cs")));
        Assert.Contains("public void Work()", updated, StringComparison.Ordinal);
        Assert.Contains("public void OuterWork()", updated, StringComparison.Ordinal);
        Assert.Contains("public int NestedWork()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Skip(", updated, StringComparison.Ordinal);
        var pointStart = updated.IndexOf("public struct PointSkip", StringComparison.Ordinal);
        Assert.True(pointStart >= 0);
        var pointEnd = updated.IndexOf('}', pointStart);
        Assert.DoesNotContain("Work()", updated[pointStart..(pointEnd + 1)], StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("FileB.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            SourceFile = workspace.PathFor("FileA.cs")
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        Assert.Contains("public void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileA.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("FileB.cs"));
        var flipped = FlipPathCasing(workspace.PathFor("FileA.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        Assert.Contains("public void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileA.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_ReplaceExisting_ReplacesMatchingImplementation()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", AlreadyImplementedMethodSource));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("Widget.cs")));
        Assert.Equal(1, CountOccurrences(updated, "public void DoWork()"));
        Assert.DoesNotContain("old-body", updated, StringComparison.Ordinal);
        Assert.Contains("NotImplementedException", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_ThrowNotImplementedFalse_UsesDefaultBodies()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        Assert.Contains("public void WorkA()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("NotImplementedException", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_ExplicitImplementation_EmitsExplicitStubs()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", EligibleFileA));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true,
            ExplicitImplementation = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        var method = FindMethod(updated, "FileA", "WorkA");
        Assert.NotNull(method);
        Assert.NotNull(method!.ExplicitInterfaceSpecifier);
        Assert.Contains("IFileA", method.ExplicitInterfaceSpecifier!.Name.ToString());
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_DoesNotAddUndeclaredInterfaceViaTypeResolver()
    {
        await using var workspace = await TempWorkspace.CreateAsync(
            ("Types.cs", UndeclaredInterfaceSource));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("Types.cs")));
        Assert.Contains("public void Known()", updated, StringComparison.Ordinal);
        Assert.NotNull(FindMethod(updated, "Eligible", "Known"));
        Assert.Null(FindMethod(updated, "Eligible", "HuntMe"));
        Assert.Null(FindMethod(updated, "NoInterface", "Known"));
        Assert.Null(FindMethod(updated, "NoInterface", "HuntMe"));
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_SameNamedFileLocalTypes_BothGetStubs()
    {
        const string fileA = """
            namespace TestApp;

            public interface IWorker
            {
                void Work();
            }

            file class Worker : IWorker
            {
            }
            """;

        const string fileB = """
            namespace TestApp;

            file class Worker : IWorker
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("FileA.cs", fileA),
            ("FileB.cs", fileB));
        var operation = new ImplementInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileA.cs")));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("FileB.cs")));
        Assert.Contains("public void Work()", updatedA, StringComparison.Ordinal);
        Assert.Contains("public void Work()", updatedB, StringComparison.Ordinal);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileA.cs")));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("FileB.cs")));
    }

    [SkippableFact]
    public async Task ImplementInterface_AllFilesTrue_GenuinePartial_ImplementedOnce()
    {
        const string partA = """
            namespace TestApp;

            public interface IWidget
            {
                void Draw();
            }

            public partial class Widget : IWidget
            {
            }
            """;

        const string partB = """
            namespace TestApp;

            public partial class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.PartA.cs", partA),
            ("Widget.PartB.cs", partB));
        var operation = new ImplementInterfaceOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.PathFor("Widget.PartB.cs"));

        var result = await operation.ExecuteAsync(new ImplementInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.PathFor("Widget.PartA.cs")));
        Assert.Contains("public void Draw()", updatedA, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updatedA, "public void Draw()"));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.PathFor("Widget.PartB.cs")));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.PathFor("Widget.PartA.cs")));
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesNestedAndInterface()
    {
        var root = CSharpSyntaxTree.ParseText(NormalizeNewlines(MixedEligibleAndSkipped)).GetRoot();
        var types = ImplementInterfaceOperation.CollectTypeDeclarations(root);
        var names = types.Select(t => t.Identifier.Text).ToList();
        Assert.Contains("Eligible", names);
        Assert.Contains("StaticSkip", names);
        Assert.Contains("ISkip", names);
        Assert.Contains("PointSkip", names);
        Assert.Contains("Outer", names);
        Assert.Contains("Nested", names);
        Assert.Contains("INested", names);
        Assert.True(names.IndexOf("Outer") < names.IndexOf("Nested"));
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

    private static IReadOnlyList<ClassDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasMethod(ClassDeclarationSyntax type, string methodName) =>
        type.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == methodName);

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string FlipPathCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i])
                    ? char.ToLowerInvariant(chars[i])
                    : char.ToUpperInvariant(chars[i]);
                break;
            }
        }

        return new string(chars);
    }

    // Single-line snippets only — IndexOf of an LF-only snippet missed
    // CRLF checkouts (FindMethod_ColumnOnContinuationLine on #200 / #214).
    private static int FindLine(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
                line++;
        }

        return line;
    }

    private static int ColumnOf(string source, string snippet)
    {
        source = NormalizeNewlines(source);
        snippet = NormalizeNewlines(snippet);
        var index = source.IndexOf(snippet, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException($"Snippet not found: {snippet}");

        var lineStart = source.LastIndexOf('\n', index);
        return index - lineStart;
    }

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

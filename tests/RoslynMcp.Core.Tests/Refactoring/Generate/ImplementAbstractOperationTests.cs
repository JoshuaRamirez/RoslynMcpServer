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
/// Operation-level tests for <see cref="ImplementAbstractOperation"/>.
/// </summary>
public class ImplementAbstractOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = "",
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = ""
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = "Types.cs",
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void ThrowNotImplemented_DefaultsToTrue()
    {
        var @params = new ImplementAbstractParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget"
        };

        Assert.True(@params.ThrowNotImplemented);
        Assert.False(@params.ReplaceExisting);
        Assert.False(@params.Preview);
        Assert.False(@params.AllFiles);
        Assert.Null(@params.Line);
        Assert.Null(@params.Column);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new ImplementAbstractParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget"
        };

        Assert.False(@params.ReplaceExisting);
    }

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new ImplementAbstractParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new ImplementAbstractParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameWidgetSource = """
        namespace TestApp;

        public abstract class Shape
        {
            public abstract void Draw();
        }

        public /* outer-widget */ class Widget : Shape
        {
            public /* nested-widget */ class Widget : Shape
            {
            }
        }
        """;

    [SkippableFact]
    public async Task ImplementAbstract_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.False(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Draw"));
        Assert.True(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "outer-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.False(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Implement abstract members on 'Widget'", result.PendingChanges[0].Description);
        Assert.Contains("Draw", result.PendingChanges[0].Description);
        Assert.Contains("void Draw()", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "nested-widget"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "outer-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class
                Widget : Shape // split-widget
            {
                public class Widget : Shape // nested-widget
                {
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    private const string EnumFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* widget-enum */ enum Widget
            {
                Ready
            }
        }

        namespace TestApp
        {
            public abstract class Shape
            {
                public abstract void Draw();
            }

            public /* widget-class */ class Widget : Shape
            {
            }
        }
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(EnumFirstThenSameNamedClassSource, "widget-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(EnumFirstThenSameNamedClassSource, "widget-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_OmittedLine_EnumFirstThenSameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override void Draw", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = FindLine(EnumFirstThenSameNamedClassSource, "widget-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override void Draw", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnClassIdentifier_SameNamedEnum_ImplementsOnClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(EnumFirstThenSameNamedClassSource, "widget-class")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Widget");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("enum Widget", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("override void Draw", updated[..updated.IndexOf("class Widget", StringComparison.Ordinal)]);
    }

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* widget-delegate */ delegate void Widget();
        }

        namespace TestApp
        {
            public abstract class Shape
            {
                public abstract void Draw();
            }

            public /* widget-class */ class Widget : Shape
            {
            }
        }
        """;

    private const string DelegateOnlySource = """
        namespace TestApp;

        public /* widget-delegate */ delegate void Widget();
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(DelegateFirstThenSameNamedClassSource, "widget-delegate"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateOnly_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateOnlySource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", FindLine(DelegateOnlySource, "widget-delegate"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateOnly_IsNotFound()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateOnlySource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_OmittedLine_DelegateFirstThenSameNamedClass_ImplementsOnClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Widget");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("delegate void Widget()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("override void Draw", updated[..updated.IndexOf("class Widget", StringComparison.Ordinal)]);
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "widget-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override void Draw", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnDelegateOnly_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateOnlySource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = FindLine(DelegateOnlySource, "widget-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.NotEqual(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(ImplementAbstractOperation.SpanCoversLine(span, 1));
        Assert.True(ImplementAbstractOperation.SpanCoversLine(span, 2));
        Assert.False(ImplementAbstractOperation.SpanCoversLine(span, 3));
        Assert.False(ImplementAbstractOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task ImplementAbstract_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
    {
        const string source = """
            public abstract class Shape
            {
                public abstract void Draw();
            }

            namespace Other
            {
                public class Widget : Shape
                {
                }
            }

            namespace TestApp
            {
                public partial class Widget : Shape
                {
                    public override void Draw() { /* old-body */ }
                }

                public /* later-partial */ partial class Widget
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(source, "later-partial"),
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasMethod(types[0], "Draw"));
        Assert.False(TypeHasMethod(types[1], "Draw"));
        Assert.True(TypeHasMethod(types[2], "Draw"));
        Assert.DoesNotContain("old-body", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new global::System.NotImplementedException()", types[2].ToFullString(), StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(await File.ReadAllTextAsync(workspace.SourcePath), "public override void Draw("));
    }

    [SkippableFact]
    public async Task ImplementAbstract_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public abstract class AlphaBase
            {
                public abstract void AlphaWork();
            }

            public abstract class BetaBase
            {
                public abstract void BetaWork();
            }

            public class Alpha : AlphaBase
            {
                public override void AlphaWork() { /* old-alpha */ }
            }

            public class Beta : BetaBase
            {
                public override void BetaWork() { /* old-beta */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta",
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var types = GetTypes(updated, "Alpha").Concat(GetTypes(updated, "Beta")).ToList();
        var alpha = types.Single(t => t.Identifier.Text == "Alpha");
        var beta = types.Single(t => t.Identifier.Text == "Beta");
        Assert.True(TypeHasMethod(alpha, "AlphaWork"));
        Assert.True(TypeHasMethod(beta, "BetaWork"));
        Assert.False(TypeHasMethod(alpha, "BetaWork"));
        Assert.False(TypeHasMethod(beta, "AlphaWork"));
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Contains("throw new global::System.NotImplementedException()", alpha.ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new global::System.NotImplementedException()", beta.ToFullString(), StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alpha.ToFullString()), "public override void AlphaWork("));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(beta.ToFullString()), "public override void BetaWork("));
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedWidgetSource = """
        namespace TestApp;

        public abstract class Shape
        {
            public abstract void Draw();
        }

        public class Widget : Shape { public class Widget : Shape { } }
        """;

    [SkippableFact]
    public async Task ImplementAbstract_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.False(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Draw"));
        Assert.True(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_OmittedColumn_EnumFirstThenSameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override void Draw", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : Shape { public class");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { }");

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasMethod(types[0], "Draw"));
        Assert.True(TypeHasMethod(types[1], "Draw"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : Shape { public class");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { public class");

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.False(TypeHasMethod(types[1], "Draw"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : Shape { public class");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { }"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : Shape { public class");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { public class"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { }");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Widget()");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class
                Widget : Shape // split-widget
            {
                public class Widget : Shape // nested-widget
                {
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", identifierLine, ColumnOf(source, "Widget : Shape // split-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class
                Widget : Shape // split-widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Widget");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget : Shape // split-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Single(types);
        Assert.True(TypeHasMethod(types[0], "Draw"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Widget { Ready } public class Widget { } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Widget");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(source, "Widget { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public enum Widget { Ready } public class Widget { } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(source, "public enum Widget");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = line,
                Column = ColumnOf(source, "Widget { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("override void Draw", await File.ReadAllTextAsync(workspace.SourcePath), StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var line = FindLine(DelegateFirstThenSameNamedClassSource, "widget-delegate");
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(DelegateFirstThenSameNamedClassSource, "Widget()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(DelegateFirstThenSameNamedClassSource, "widget-delegate");

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = line,
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Widget()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("override void Draw", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(root, "Widget", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ColumnAndLine_UnknownTypeName_ThrowsSymbolNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_UnknownTypeName_ReportsEmptyCandidateSet()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Missing", line: 1, column: 1, out var hadCandidates);

        Assert.Null(found);
        Assert.False(hadCandidates);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_ReportsCandidatesExisted()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line: 1, column: 1, out var hadCandidates);

        Assert.Null(found);
        Assert.True(hadCandidates);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLine_DelegateOnlyMiss_ReportsCandidatesExisted()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateOnlySource).GetRoot();
        var found = ImplementAbstractOperation.FindTypeDeclaration(
            root, "Widget", line: 1, column: 1, out var hadCandidates);

        Assert.Null(found);
        Assert.True(hadCandidates);
    }

    [SkippableFact]
    public async Task ImplementAbstract_Column_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget : Shape { public class");

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineNestedWidgetSource, "Widget : Shape { }"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Implement abstract members on 'Widget'", result.PendingChanges[0].Description);
        Assert.Contains("Draw", result.PendingChanges[0].Description);
        Assert.Contains("void Draw()", result.PendingChanges[0].AfterSnippet);
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

        Assert.True(ImplementAbstractOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(ImplementAbstractOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(ImplementAbstractOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(ImplementAbstractOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ImplementAbstract_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Widget : Shape { public override void Draw() { /* old-outer */ } public class Widget : Shape { public override void Draw() { /* old-nested */ } } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var line = FindLine(source, "public class Widget : Shape { public override void Draw() { /* old-outer */ }");

        var first = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(source, "Widget : Shape { public override void Draw() { /* old-outer */ }"),
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(afterFirst, "old-nested"),
            Column = ColumnOf(afterFirst, "Widget : Shape { public override void Draw() { /* old-nested */ }"),
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasMethod(types[0], "Draw"));
        Assert.True(TypeHasMethod(types[1], "Draw"));
        Assert.DoesNotContain("old-outer", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-nested", types[1].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new global::System.NotImplementedException()", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.Contains("throw new global::System.NotImplementedException()", types[1].ToFullString(), StringComparison.Ordinal);
        Assert.Single(types[0].Members.OfType<MethodDeclarationSyntax>());
        Assert.Single(types[1].Members.OfType<MethodDeclarationSyntax>());
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task ImplementAbstract_Method_AddsOverrideStub()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Circle", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_Property_AddsOverrideAccessors()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Area { get; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int Area", updated);
        Assert.Contains("get", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("set", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_MethodAndProperty_AddsBoth()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract string Name { get; set; }

                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override string Name", updated);
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedMember_PreservesAccessibility()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                protected abstract void Paint();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected override void Paint()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AlreadyImplementedMember_GeneratesRemaining()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Draw", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("stubs will throw NotImplementedException", result.PendingChanges[0].Description);
        Assert.DoesNotContain("stubs will not throw", result.PendingChanges[0].Description);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_MembersFilter_ImplementsRequestedOnly()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Members = new[] { "Resize" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize()", updated);
        Assert.DoesNotContain("public override void Draw()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_Event_AddsOverrideStub()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        Assert.Equal(1, CountOccurrences(
            updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..],
            "event System.EventHandler Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_OnlyEvents_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
                public abstract event System.EventHandler Resized;
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.Contains("public override event System.EventHandler Resized", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_MembersFilter_Event_ImplementsRequestedOnly()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
                public abstract event System.EventHandler Resized;
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Members = new[] { "Changed" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("public override event System.EventHandler Resized", updated);
        Assert.DoesNotContain("public override void Draw()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_MembersFilter_UnknownEventName_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_Event_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.Contains("Changed", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("event", result.PendingChanges[0].AfterSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AlreadyImplementedEvent_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedEventSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-event", before);
    }

    [SkippableFact]
    public async Task ImplementAbstract_NewHidingEvent_IsLeftAlone()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
                public abstract event System.EventHandler Resized;
            }

            public class Circle : Shape
            {
                public new event System.EventHandler Changed
                {
                    add { /* hid */ }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("add { /* hid */ }", updated);
        Assert.Contains("public new event System.EventHandler Changed", updated);
        Assert.Contains("public override event System.EventHandler Resized", updated);
        Assert.DoesNotContain("public override event System.EventHandler Changed", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_IntermediateConcreteEventOverride_HidesInheritedAbstract()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
            }

            public class Intermediate : Shape
            {
                public override event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Circle : Intermediate
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_IntermediateNewVirtualEventHider_DoesNotEmitOverride()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
                public abstract event System.EventHandler Resized;
            }

            public abstract class Intermediate : Shape
            {
                public new virtual event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Circle : Intermediate
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..];
        Assert.Contains("public override event System.EventHandler Resized", derived);
        Assert.DoesNotContain("public override event System.EventHandler Changed", derived);
        Assert.Contains("public new virtual event System.EventHandler Changed", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_IntermediateNewVirtualEventHider_OnlyHiddenEvent_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
            }

            public abstract class Intermediate : Shape
            {
                public new virtual event System.EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }

            public class Circle : Intermediate
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ExplicitInterfaceEvent_DoesNotCountAsImplementation()
    {
        const string source = """
            namespace TestApp;

            public interface INotify
            {
                event System.EventHandler Changed;
            }

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
            }

            public class Circle : Shape, INotify
            {
                event System.EventHandler INotify.Changed
                {
                    add { /* explicit */ }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("event System.EventHandler INotify.Changed", updated);
        Assert.Contains("/* explicit */", updated);
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.Equal(1, CountOccurrences(updated, "INotify.Changed"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplemented_DoesNotApplyToEvents()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MissingAbstractEventSource);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var evt = ExtractMember(updated, "public override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
        Assert.DoesNotContain("NotImplementedException", evt);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedEvent_SameAssembly_KeepsProtected()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                protected abstract event System.EventHandler Changed;
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("protected internal override", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedInternalEvent_SameAssembly_KeepsProtectedInternal()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                protected internal abstract event System.EventHandler Changed;
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected internal override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("protected override event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_InternalEvent_SameAssembly_KeepsInternal()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                internal abstract event System.EventHandler Changed;
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("internal override event System.EventHandler Changed", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_ProtectedInternalEvent_EmitsProtected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                protected internal abstract event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..];
        Assert.Contains("protected override event System.EventHandler Changed", derived);
        Assert.DoesNotContain("protected internal override", derived);
        Assert.DoesNotContain("public override event", derived);
        var evt = ExtractMember(updated, "protected override event System.EventHandler Changed");
        Assert.Contains("add", evt);
        Assert.Contains("remove", evt);
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_ProtectedInternalEvent_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                protected internal abstract event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Changed", result.PendingChanges[0].Description);
        Assert.Contains("protected override event", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("protected internal override", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_InternalEvent_IsNotSelected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                internal abstract event System.EventHandler Hidden;
                public abstract event System.EventHandler Changed;
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var namedHidden = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle",
                Members = new[] { "Hidden" }
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, namedHidden.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("Hidden", updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..]);
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_PrivateProtectedEvent_IsNotSelected()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                private protected abstract event System.EventHandler Hidden;
                public abstract void Draw();
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var namedHidden = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle",
                Members = new[] { "Hidden" }
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, namedHidden.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.DoesNotContain("Hidden", updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..]);
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_OnlyInaccessibleEvent_Throws()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                internal abstract event System.EventHandler Hidden;
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedInternalProperty_ProtectedSetter_SameAssembly_KeepsBoth()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                protected internal abstract int Width { get; protected set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("protected internal override int Width", updated);
        Assert.Contains("protected set", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_CrossAssembly_ProtectedInternalProperty_ProtectedSetter_OmitsRedundantAccessor()
    {
        await using var workspace = await TempWorkspace.CreateReferencedLibraryAsync(
            """
            namespace TestLib;

            public abstract class Shape
            {
                protected internal abstract int Width { get; protected set; }
            }
            """,
            """
            namespace TestApp;

            public class Circle : TestLib.Shape
            {
            }
            """);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var derived = updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..];
        Assert.Contains("protected override int Width", derived);
        Assert.DoesNotContain("protected internal override", derived);
        Assert.DoesNotContain("protected set", derived);
        var property = ExtractMember(updated, "protected override int Width");
        Assert.Contains("get", property);
        Assert.Contains("set", property);
    }

    #endregion

    #region ThrowNotImplemented

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedTrue_UsesThrowBodies()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_Method_UsesDefaultReturnBody()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract int Count();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("public override int Count()", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_RefReturnMethod_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract ref int GetCell();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override ref int GetCell()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_RefReadonlyReturnMethod_StillThrows()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract ref readonly int GetOrigin();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override ref readonly int GetOrigin()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_RefPropertyAndIndexerGetters_StillThrow()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract ref int Cell { get; }
                public abstract ref int this[int index] { get; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override ref int Cell", updated);
        Assert.Contains("public override ref int this[int index]", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.DoesNotContain("return default", updated);
        Assert.DoesNotContain("return null", updated);
        Assert.Equal(2, CountOccurrences(updated, "throw new global::System.NotImplementedException();"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_Property_GetterDefaultSetterEmpty()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract string Name { get; set; }
                public abstract int Area { get; set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override string Name", updated);
        Assert.Contains("public override int Area", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return null;", updated);
        Assert.Contains("return default(int);", updated);

        var nameProperty = ExtractMember(updated, "public override string Name");
        Assert.Contains("get", nameProperty);
        Assert.Contains("set", nameProperty);
        Assert.Contains("return null;", nameProperty);
        Assert.DoesNotContain("return", ExtractAccessor(nameProperty, "set"));

        var areaProperty = ExtractMember(updated, "public override int Area");
        Assert.Contains("return default(int);", ExtractAccessor(areaProperty, "get"));
        Assert.DoesNotContain("return", ExtractAccessor(areaProperty, "set"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_Indexer_SameAccessorRules()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int this[int index] { get; set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int this[int index]", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);

        var indexer = ExtractMember(updated, "public override int this[int index]");
        Assert.Contains("return default(int);", ExtractAccessor(indexer, "get"));
        Assert.DoesNotContain("return", ExtractAccessor(indexer, "set"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_InitSetter_EmptyInitNotSet()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Area { get; init; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int Area", updated);
        Assert.Contains("init", updated);
        Assert.DoesNotContain("set", updated);
        Assert.DoesNotContain("NotImplementedException", updated);
        Assert.Contains("return default(int);", updated);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImplementAbstract_MembersFilter_WorksWithEitherThrowFlag(bool throwNotImplemented)
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Members = new[] { "Resize" },
            ThrowNotImplemented = throwNotImplemented
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize()", updated);
        Assert.DoesNotContain("public override void Draw()", updated);
        if (throwNotImplemented)
            Assert.Contains("throw new global::System.NotImplementedException();", updated);
        else
            Assert.DoesNotContain("NotImplementedException", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ThrowNotImplementedFalse_Preview_DoesNotWriteAndDescribesNonThrowingStubs()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("stubs will not throw", result.PendingChanges[0].Description);
        Assert.DoesNotContain("NotImplementedException", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("Draw", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Review Fold

    [SkippableFact]
    public async Task ImplementAbstract_InitSetter_EmitsInitNotSet()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Area { get; init; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int Area", updated);
        Assert.Contains("init", updated);
        Assert.DoesNotContain("set", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ProtectedSetter_PreservesAccessorAccessibility()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract string Name { get; protected set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override string Name", updated);
        Assert.Contains("protected set", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_RefKindOverloads_GeneratesBoth()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Resize(int size);
                public abstract void Resize(ref int size);
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Resize(int size)", updated);
        Assert.Contains("public override void Resize(ref int size)", updated);
        Assert.Equal(2, CountOccurrences(updated, "public override void Resize("));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ByRefReturns_PreservesRefAndRefReadonly()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract ref int GetCell();
                public abstract ref readonly int GetOrigin();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override ref int GetCell()", updated);
        Assert.Contains("public override ref readonly int GetOrigin()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_Indexer_AddsOverrideStub()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int this[int index] { get; set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int this[int index]", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_IndexerOverloads_GeneratesBoth()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int this[int index] { get; }
                public abstract int this[string name] { get; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int this[int index]", updated);
        Assert.Contains("public override int this[string name]", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_RequiredProperty_KeepsRequired()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract required string Name { get; set; }
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("required", updated);
        Assert.Contains("public override required string Name", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    #endregion

    #region replaceExisting false / omitted

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingOmitted_AlreadyFullyImplemented_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-body", before);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingFalse_AlreadyFullyImplemented_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingFalse_AlreadyImplementedMember_GeneratesRemaining()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                    /* old-body */
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("old-body", updated);
        Assert.Contains("public override void Resize()", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
    }

    #endregion

    #region replaceExisting true

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ReplacesMethod()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void Draw()", updated);
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ReplacesProperty()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Area { get; set; }
            }

            public class Circle : Shape
            {
                public override int Area { get; set; } = 42;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override int Area", updated);
        Assert.DoesNotContain("= 42", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override int Area"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ReplacesIndexer()
    {
        const string source = """
            namespace TestApp;

            public abstract class Lookup
            {
                public abstract string this[int i] { get; set; }
            }

            public class FastLookup : Lookup
            {
                public override string this[int i]
                {
                    get => "old";
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "FastLookup",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override string this[int i]", updated);
        Assert.DoesNotContain("old", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        var derived = updated[updated.IndexOf("public class FastLookup", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOccurrences(derived, "this[int i]"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ReplacesEvent()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract event System.EventHandler Changed;
            }

            public class Circle : Shape
            {
                public override event System.EventHandler Changed
                {
                    add { /* old-event */ }
                    remove { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override event System.EventHandler Changed", updated);
        Assert.DoesNotContain("old-event", updated);
        Assert.Contains("add", updated);
        Assert.Contains("remove", updated);
        Assert.Equal(1, CountOccurrences(
            updated[updated.IndexOf("public class Circle", StringComparison.Ordinal)..],
            "event System.EventHandler Changed"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_OmittedMembers_ReplacesExisting_AndAddsMissing()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                    /* old-body */
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("public override void Resize()", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_NamedMembers_OnlyReplacesThose()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                    /* old-body */
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            Members = new[] { "Draw" },
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("public override void Draw()", updated);
        Assert.DoesNotContain("public override void Resize()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ThrowNotImplementedTrue_ReplacesWithThrow()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ThrowNotImplemented = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ThrowNotImplementedFalse_ReplacesWithDefaultReturn()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract int Size();
            }

            public class Circle : Shape
            {
                public override int Size() => 99;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
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
    public async Task ImplementAbstract_ReplaceExistingTrue_PartialOtherFile_RemovesThere_InsertsOnTarget()
    {
        const string typePart = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public partial class Circle : Shape
            {
            }
            """;

        const string implPart = """
            namespace TestApp;

            public partial class Circle
            {
                public override void Draw()
                {
                    /* old-partial */
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Circle.cs", typePart),
            ("Circle.Impl.cs", implPart));
        var otherPath = workspace.PathFor("Circle.Impl.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        Assert.Contains("public override void Draw()", selected);
        Assert.Contains("throw new global::System.NotImplementedException();", selected);
        Assert.DoesNotContain("old-partial", selected);
        Assert.DoesNotContain("void Draw(", other);
        Assert.DoesNotContain("old-partial", other);
        Assert.Equal(1, CountOccurrences(selected, "public override void Draw()"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_Preview_WritesNothing_AndMentionsReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("replace existing", result.PendingChanges[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Draw", result.PendingChanges[0].Description);
        Assert.Contains("stubs will throw NotImplementedException", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing abstract members", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("throw new global::System.NotImplementedException()", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("old-body", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string typePart = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public partial class Circle : Shape
            {
            }
            """;

        const string implPart = """
            namespace TestApp;

            public partial class Circle
            {
                public override void Draw()
                {
                    /* old-partial */
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Circle.cs", typePart),
            ("Circle.Impl.cs", implPart));
        var otherPath = workspace.PathFor("Circle.Impl.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
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
        Assert.Contains("Remove existing method 'Draw'", otherChange.Description);
        Assert.Contains("old-partial", otherChange.BeforeSnippet);
        Assert.Equal("// method removed", otherChange.AfterSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_AmbiguousSameName_NameCollision_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public abstract class Handler
            {
                public abstract void Handle(int x);
                public abstract void Handle(string s);
                public abstract void Handle(object o);
            }

            public class DefaultHandler : Handler
            {
                public override void Handle(int x) { }

                public override void Handle(string s) { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "DefaultHandler",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal("3003", ex.ErrorCode);
        Assert.Contains("Handle", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_UnknownMemberFilter_Throws()
    {
        await using var workspace = await TempWorkspace.CreateAsync(AlreadyImplementedMethodSource);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle",
                Members = new[] { "DoesNotExist" },
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_NewHidingMethod_IsNotReplaced()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
                public abstract void Resize();
            }

            public class Circle : Shape
            {
                public new void Draw() { /* hid */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public new void Draw() { /* hid */ }", updated);
        Assert.Contains("public override void Resize()", updated);
        Assert.DoesNotContain("public override void Draw()", updated);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_ExplicitInterfaceImplementation_IsNotReplaced()
    {
        const string source = """
            namespace TestApp;

            public interface IDrawable
            {
                void Draw();
            }

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape, IDrawable
            {
                void IDrawable.Draw() { /* explicit */ }

                public override void Draw() { /* old-body */ }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("void IDrawable.Draw() { /* explicit */ }", updated);
        Assert.DoesNotContain("old-body", updated);
        Assert.Contains("public override void Draw()", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_IfDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            #if DEBUG
                public override void Draw() { /* old-if */ }
            #endif

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#if DEBUG", updated);
        Assert.Contains("#endif", updated);
        Assert.Contains("void Draw()", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old-if", updated);
        Assert.Equal(updated.Split("#if ").Length - 1, updated.Split("#endif").Length - 1);
    }

    [SkippableFact]
    public async Task ImplementAbstract_ReplaceExistingTrue_RegionDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            #region Work
                public override void Draw() { /* old-region */ }
            #endregion

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Circle",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#region Work", updated);
        Assert.Contains("#endregion", updated);
        Assert.Contains("void Draw()", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old-region", updated);
        Assert.Equal(updated.Split("#region ").Length - 1, updated.Split("#endregion").Length - 1);
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task ImplementAbstract_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_NoUnimplementedAbstractMembers_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AlreadyFullyImplemented_Throws()
    {
        const string source = """
            namespace TestApp;

            public abstract class Shape
            {
                public abstract void Draw();
            }

            public class Circle : Shape
            {
                public override void Draw()
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Circle"
            }));

        Assert.Equal(ErrorCodes.NoUnimplementedAbstractMembers, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_UnsupportedTarget_Enum_Throws()
    {
        const string source = """
            namespace TestApp;

            public enum Status
            {
                Ready
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Status"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ImplementAbstract_UnsupportedTarget_Interface_Throws()
    {
        const string source = """
            namespace TestApp;

            public interface IDrawable
            {
                void Draw();
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new ImplementAbstractOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "IDrawable"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void ImplementAbstract_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    #endregion

    #region AllFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public abstract class BaseA
        {
            public abstract void WorkA();
        }

        public class FileA : BaseA
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

        public abstract class BaseB
        {
            public abstract int Value { get; }
        }

        public class FileB : BaseB
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

        public abstract class Shape
        {
            public abstract void Draw();
        }

        public class AlreadyImplemented : Shape
        {
            public override void Draw() { }
        }

        public class Empty
        {
        }
        """;

    private const string MixedEligibleAndSkipped = """
        namespace TestApp;

        public abstract class Base
        {
            public abstract void Work();
        }

        public class Eligible : Base
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

        public abstract class OuterBase
        {
            public abstract void OuterWork();
        }

        public class Outer : OuterBase
        {
            public abstract class NestedBase
            {
                public abstract int NestedWork();
            }

            public class Nested : NestedBase
            {
            }
        }
        """;

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                AllFiles = false,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                AllFiles = false,
                SourceFile = AbsoluteTestPath()
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrTypeName_DoesNotThrow()
    {
        ImplementAbstractOperation.Validate(new ImplementAbstractParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
            {
                AllFiles = true,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithMembers_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
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
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
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

        var keyA = ImplementAbstractOperation.TypeWalkKey(projectA, fqn);
        var keyB = ImplementAbstractOperation.TypeWalkKey(projectB, fqn);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(keyA, ImplementAbstractOperation.TypeWalkKey(projectA, fqn));
        Assert.NotEqual(keyA, ImplementAbstractOperation.TypeWalkKey(projectA, "global::TestApp.Other"));
    }

    [Fact]
    public void TypeWalkKey_FileLocalIdentity_DistinguishesSameFqn()
    {
        var project = ProjectId.CreateNewId();
        const string fqn = "global::TestApp.Worker";

        var ordinary = ImplementAbstractOperation.TypeWalkKey(project, fqn);
        var fileA = ImplementAbstractOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs");
        var fileB = ImplementAbstractOperation.TypeWalkKey(project, fqn, "/tmp/FileB.cs");

        Assert.NotEqual(ordinary, fileA);
        Assert.NotEqual(ordinary, fileB);
        Assert.NotEqual(fileA, fileB);
        Assert.Equal(fileA, ImplementAbstractOperation.TypeWalkKey(project, fqn, "/tmp/FileA.cs"));
        Assert.Equal(ordinary, ImplementAbstractOperation.TypeWalkKey(project, fqn));
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
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
            ImplementAbstractOperation.Validate(new ImplementAbstractParams
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
        Assert.Equal("Implement abstract members", ImplementAbstractOperation.BuildAllFilesDescription(1));
        Assert.Equal("Implement abstract members on 2 types", ImplementAbstractOperation.BuildAllFilesDescription(2));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesFalse_ImplementsOnlySpecifiedType()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePaths["FileA.cs"],
            AllFiles = false,
            TypeName = "FileA"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public override void Skip(", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_OmittedAllFiles_KeepsSingleSiteImplement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "FileA"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public override void WorkA()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public override void Skip(", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_ImplementsEligibleTypesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public override void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.DoesNotContain("public override void Skip(", updatedA, StringComparison.Ordinal);
        Assert.Contains("public override int Value", updatedB, StringComparison.Ordinal);
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_WithoutSourceFileOrTypeName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = false,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesFalse_WithoutTypeName_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = false,
                SourceFile = workspace.SourcePath
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("typeName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_WithMembers_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = true,
                Members = new[] { "WorkA" }
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("members", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_WithLine_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = true,
                Line = 8
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_WithColumn_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA, "FileA.cs");
        var operation = new ImplementAbstractOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ImplementAbstractParams
            {
                AllFiles = true,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ImplementAbstract_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleFileC));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.PendingChanges, c => PathEquals(c.File, workspace.SourcePaths["FileC.cs"]));
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Implement", StringComparison.OrdinalIgnoreCase) &&
            c.AfterSnippet != null &&
            (c.AfterSnippet.Contains("WorkA", StringComparison.Ordinal) ||
             c.AfterSnippet.Contains("Value", StringComparison.Ordinal)));
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleFileC),
            ("FileC2.cs", IneligibleFileC
                .Replace("Limits", "Limits2", StringComparison.Ordinal)
                .Replace("IWidget", "IWidget2", StringComparison.Ordinal)
                .Replace("Point", "Point2", StringComparison.Ordinal)
                .Replace("Shape", "Shape2", StringComparison.Ordinal)
                .Replace("AlreadyImplemented", "AlreadyImplemented2", StringComparison.Ordinal)
                .Replace("Empty", "Empty2", StringComparison.Ordinal)));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Changes);
        Assert.Empty(result.Changes.FilesModified);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileC2.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_SkipsStaticInterfaceStructAndNoUnimplemented()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Mixed.cs", MixedEligibleAndSkipped));
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Mixed.cs"]));
        Assert.Contains("public override void Work()", updated, StringComparison.Ordinal);
        Assert.Contains("public override void OuterWork()", updated, StringComparison.Ordinal);
        Assert.Contains("public override int NestedWork()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public override void Skip(", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("static override", updated, StringComparison.Ordinal);
        var pointStart = updated.IndexOf("public struct PointSkip", StringComparison.Ordinal);
        Assert.True(pointStart >= 0);
        var pointEnd = updated.IndexOf('}', pointStart);
        Assert.DoesNotContain("override", updated[pointStart..(pointEnd + 1)], StringComparison.Ordinal);
        Assert.Single(result.Changes!.FilesModified);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_OptionalSourceFile_MatchesIgnoreCase()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);
        var flipped = FlipPathCasing(workspace.SourcePaths["FileA.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true,
            SourceFile = flipped
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override void WorkA()", updatedA, StringComparison.Ordinal);
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_ReplaceExisting_ReplacesMatchingImplementation()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Widget.cs", AlreadyImplementedMethodSource));
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Widget.cs"]));
        Assert.Equal(1, CountOccurrences(updated, "public override void Draw()"));
        Assert.DoesNotContain("old-body", updated, StringComparison.Ordinal);
        Assert.Contains("NotImplementedException", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_ThrowNotImplementedFalse_UsesDefaultBodies()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA));
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true,
            ThrowNotImplemented = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Contains("public override void WorkA()", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("NotImplementedException", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_SameNamedFileLocalTypes_BothGetStubs()
    {
        const string fileA = """
            namespace TestApp;

            public abstract class WorkerBase
            {
                public abstract void Work();
            }

            file class Worker : WorkerBase
            {
            }
            """;

        const string fileB = """
            namespace TestApp;

            file class Worker : WorkerBase
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", fileA),
            ("FileB.cs", fileB));
        var operation = new ImplementAbstractOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains("public override void Work()", updatedA, StringComparison.Ordinal);
        Assert.Contains("public override void Work()", updatedB, StringComparison.Ordinal);
        Assert.Equal(2, result.Changes!.FilesModified.Count);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
    }

    [SkippableFact]
    public async Task ImplementAbstract_AllFilesTrue_GenuinePartial_ImplementedOnce()
    {
        const string partA = """
            namespace TestApp;

            public abstract class PartialBase
            {
                public abstract void Draw();
            }

            public partial class Widget : PartialBase
            {
            }
            """;

        const string partB = """
            namespace TestApp;

            public partial class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("Widget.PartA.cs", partA),
            ("Widget.PartB.cs", partB));
        var operation = new ImplementAbstractOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartB.cs"]);

        var result = await operation.ExecuteAsync(new ImplementAbstractParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartA.cs"]));
        Assert.Contains("public override void Draw()", updatedA, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updatedA, "public override void Draw()"));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["Widget.PartB.cs"]));
        Assert.Single(result.Changes!.FilesModified);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["Widget.PartA.cs"]));
    }

    [Fact]
    public void CollectTypeDeclarations_IncludesNestedAndInterface()
    {
        var root = CSharpSyntaxTree.ParseText(NormalizeNewlines(MixedEligibleAndSkipped)).GetRoot();
        var types = ImplementAbstractOperation.CollectTypeDeclarations(root);
        var names = types.Select(t => t.Identifier.Text).ToList();
        Assert.Contains("Eligible", names);
        Assert.Contains("StaticSkip", names);
        Assert.Contains("ISkip", names);
        Assert.Contains("PointSkip", names);
        Assert.Contains("Outer", names);
        Assert.Contains("Nested", names);
        Assert.True(names.IndexOf("Outer") < names.IndexOf("Nested"));
    }

    #endregion

    #region Helpers

    private const string AlreadyImplementedMethodSource = """
        namespace TestApp;

        public abstract class Shape
        {
            public abstract void Draw();
        }

        public class Circle : Shape
        {
            public override void Draw()
            {
                /* old-body */
            }
        }
        """;

    private const string MissingAbstractEventSource = """
        namespace TestApp;

        public abstract class Shape
        {
            public abstract event System.EventHandler Changed;
        }

        public class Circle : Shape
        {
        }
        """;

    private const string AlreadyImplementedEventSource = """
        namespace TestApp;

        public abstract class Shape
        {
            public abstract event System.EventHandler Changed;
        }

        public class Circle : Shape
        {
            public override event System.EventHandler Changed
            {
                add { /* old-event */ }
                remove { }
            }
        }
        """;

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpImplementAbstractMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasMethod(TypeDeclarationSyntax type, string methodName) =>
        type.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == methodName);

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
                "ImplementAbstractCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source) },
                new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated implement_abstract stubs did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private static string ExtractMember(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Generated source did not contain '{signature}':\n{text}");
        var openBrace = text.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Generated member '{signature}' had no opening brace:\n{text}");
        var depth = 0;
        for (var i = openBrace; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Generated member '{signature}' was not brace-balanced:\n{text}");
    }

    private static string ExtractAccessor(string member, string accessorName)
    {
        var start = member.IndexOf(accessorName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Member did not contain accessor '{accessorName}':\n{member}");
        var openBrace = member.IndexOf('{', start);
        Assert.True(openBrace >= 0, $"Accessor '{accessorName}' had no opening brace:\n{member}");
        var depth = 0;
        for (var i = openBrace; i < member.Length; i++)
        {
            if (member[i] == '{')
                depth++;
            else if (member[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return member[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Accessor '{accessorName}' was not brace-balanced:\n{member}");
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files) =>
            CreateAsync(files);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Types.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpImplementAbstract_" + Guid.NewGuid().ToString("N"));
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
            var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePaths[fileName] = path;
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Types.cs");
            return await LoadAsync(directory, projectPath, sourcePath, sourcePaths);
        }

        /// <summary>
        /// Lib project referenced by App. <see cref="SourcePath"/> is App/Circle.cs.
        /// </summary>
        public static async Task<TempWorkspace> CreateReferencedLibraryAsync(string librarySource, string appSource)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpImplementAbstractXP_" + Guid.NewGuid().ToString("N"));
            var libDir = Path.Combine(directory, "Lib");
            var appDir = Path.Combine(directory, "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);

            var libProject = Path.Combine(libDir, "Lib.csproj");
            var appProject = Path.Combine(appDir, "App.csproj");
            var libSource = Path.Combine(libDir, "Shape.cs");
            var appSourcePath = Path.Combine(appDir, "Circle.cs");

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
            await File.WriteAllTextAsync(libSource, librarySource);
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

            return await LoadAsync(
                directory,
                solutionPath,
                appSourcePath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Circle.cs"] = appSourcePath
                });
        }

        private static async Task<TempWorkspace> LoadAsync(
            string directory,
            string projectPath,
            string sourcePath,
            IReadOnlyDictionary<string, string> sourcePaths)
        {
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

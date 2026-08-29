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
/// Operation-level tests for <see cref="GeneratePropertyOperation"/>, including
/// optional <c>line</c> and <c>replaceExisting</c>.
/// </summary>
public class GeneratePropertyOperationTests
{
    #region Input Validation

    [Fact]
    public void Validate_MissingSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = "",
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingPropertyNameAndFieldName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingPropertyTypeWithoutField_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_RelativePath_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = "Types.cs",
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_MissingFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.SourceFileNotFound, ex.ErrorCode);
    }

    [Fact]
    public void Validate_InvalidVisibility_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                Visibility = "secret"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
    }

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new GeneratePropertyParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void ReplaceExisting_DefaultsToFalse()
    {
        var @params = new GeneratePropertyParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string"
        };

        Assert.False(@params.ReplaceExisting);
    }

    [Fact]
    public void Validate_ReservedKeywordPropertyName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "class",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Fact]
    public void Validate_NamespaceKeywordPropertyName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.Validate(new GeneratePropertyParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                PropertyName = "namespace",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolName, ex.ErrorCode);
    }

    [Fact]
    public void IsValidIdentifier_ReservedKeyword_IsFalse()
    {
        Assert.False(GeneratePropertyOperation.IsValidIdentifier("class"));
        Assert.False(GeneratePropertyOperation.IsValidIdentifier("namespace"));
        Assert.True(GeneratePropertyOperation.IsValidIdentifier("Name"));
        Assert.True(GeneratePropertyOperation.IsValidIdentifier("@class"));
    }

    [Fact]
    public void ResolvePropertyToReplace_TwoSameNamedProperties_ThrowsNameCollision()
    {
        var type = CompileType("""
            public interface INamed
            {
                string Name { get; set; }
            }

            public class Widget : INamed
            {
                string INamed.Name { get; set; }

                public string Name { get; set; }
            }
            """, "Widget");

        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.ResolvePropertyToReplace(type, "Name", replaceExisting: true));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("Multiple properties named 'Name'", ex.Message);
    }

    [Fact]
    public void ResolvePropertyToReplace_SinglePublicProperty_ReturnsIt()
    {
        var type = CompileType("""
            public class Widget
            {
                public string Name { get; set; }
            }
            """, "Widget");

        var existing = GeneratePropertyOperation.ResolvePropertyToReplace(type, "Name", replaceExisting: true);

        Assert.NotNull(existing);
        Assert.Equal("Name", existing.Name);
    }

    [Fact]
    public void ResolvePropertyToReplace_OmittedFlag_ExistingProperty_Throws()
    {
        var type = CompileType("""
            public class Widget
            {
                public string Name { get; set; }
            }
            """, "Widget");

        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.ResolvePropertyToReplace(type, "Name", replaceExisting: false));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
    }

    #endregion

    #region P0 optional line disambiguation

    private const string NestedSameNameWidgetSource = """
        namespace TestApp;

        public /* outer-widget */ class Widget
        {
            public /* nested-widget */ class Widget
            {
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateProperty_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasProperty(types[0], "Name"));
        Assert.False(TypeHasProperty(types[1], "Name"));
    }

    [SkippableFact]
    public async Task GenerateProperty_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            PropertyName = "Age",
            PropertyType = "int"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasProperty(types[0], "Age"));
        Assert.True(TypeHasProperty(types[1], "Age"));
    }

    [SkippableFact]
    public async Task GenerateProperty_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "outer-widget"),
            PropertyName = "Name",
            PropertyType = "string"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasProperty(types[0], "Name"));
        Assert.False(TypeHasProperty(types[1], "Name"));
    }

    [SkippableFact]
    public async Task GenerateProperty_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget"),
            PropertyName = "Age",
            PropertyType = "int",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate property 'Age'", result.PendingChanges[0].Description);
        Assert.Contains("Widget", result.PendingChanges[0].Description);
        Assert.Contains("public int Age { get; set; }", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "nested-widget"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "outer-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Widget // split-widget
            {
                public class Widget // nested-widget
                {
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = GeneratePropertyOperation.FindTypeDeclaration(root, "Widget", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(root, "Widget", line: 1);

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
            public /* widget-class */ class Widget
            {
            }
        }
        """;

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(
            root, "Widget", FindLine(EnumFirstThenSameNamedClassSource, "widget-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = GeneratePropertyOperation.FindTypeDeclaration(
            root, "Widget", FindLine(EnumFirstThenSameNamedClassSource, "widget-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task GenerateProperty_OmittedLine_EnumFirstThenSameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("public string Name", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateProperty_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Line = FindLine(EnumFirstThenSameNamedClassSource, "widget-enum"),
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("public string Name", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateProperty_LineOnClassIdentifier_SameNamedEnum_GeneratesOnClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(EnumFirstThenSameNamedClassSource, "widget-class"),
            PropertyName = "Name",
            PropertyType = "string"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Widget");
        Assert.Single(types);
        Assert.True(TypeHasProperty(types[0], "Name"));
        Assert.Contains("public string Name { get; set; }", updated);
        Assert.Contains("enum Widget", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Name", updated[..updated.IndexOf("class Widget", StringComparison.Ordinal)]);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(GeneratePropertyOperation.SpanCoversLine(span, 1));
        Assert.True(GeneratePropertyOperation.SpanCoversLine(span, 2));
        Assert.False(GeneratePropertyOperation.SpanCoversLine(span, 3));
        Assert.False(GeneratePropertyOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task GenerateProperty_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
    {
        const string source = """
            namespace Other
            {
                public class Widget
                {
                    public string Title { get; set; }
                }
            }

            namespace TestApp
            {
                public partial class Widget
                {
                    public string Name { get; set; } = "old-partial";
                }

                public /* later-partial */ partial class Widget
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(source, "later-partial"),
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasProperty(types[0], "Name"));
        Assert.False(TypeHasProperty(types[1], "Name"));
        Assert.True(TypeHasProperty(types[2], "Name"));
        var selected = ExtractPropertyFromType(types[2], "Name");
        Assert.Contains("public int Name { get; set; }", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("old-partial", selected, StringComparison.Ordinal);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "public int Name"));
        Assert.DoesNotContain("old-partial", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("public string Name", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateProperty_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public string Name { get; set; } = "old-alpha";
            }

            public class Beta
            {
                public string Title { get; set; } = "old-beta";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta",
            PropertyName = "Title",
            PropertyType = "int",
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        var types = GetTypes(updated, "Alpha").Concat(GetTypes(updated, "Beta")).ToList();
        var alpha = types.Single(t => t.Identifier.Text == "Alpha");
        var beta = types.Single(t => t.Identifier.Text == "Beta");
        Assert.True(TypeHasProperty(alpha, "Name"));
        Assert.True(TypeHasProperty(beta, "Title"));
        Assert.False(TypeHasProperty(alpha, "Title"));
        Assert.False(TypeHasProperty(beta, "Name"));
        var alphaProp = ExtractPropertyFromType(alpha, "Name");
        var betaProp = ExtractPropertyFromType(beta, "Title");
        Assert.Contains("public int Name { get; set; }", alphaProp, StringComparison.Ordinal);
        Assert.Contains("public int Title { get; set; }", betaProp, StringComparison.Ordinal);
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alpha.ToFullString()), "public int Name"));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(beta.ToFullString()), "public int Title"));
    }

    #endregion

    #region P0 Happy Path

    [SkippableFact]
    public async Task GenerateProperty_AutoProperty_AddsGetSet()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string"
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        Assert.NotNull(result.Symbol);
        Assert.Equal("Name", result.Symbol.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Name { get; set; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_InitOnly_AddsGetInit()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Id",
            PropertyType = "int",
            InitOnly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Id { get; init; }", updated);
        Assert.DoesNotContain("{ get; set; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_BackingField_AddsExpressionAccessors()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private string _name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_name"
        });

        Assert.True(result.Success);
        Assert.Equal("Name", result.Symbol!.Name);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string _name;", updated);
        Assert.Contains("get => _name;", updated);
        Assert.Contains("set => _name = value;", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_Preview_DoesNotWriteFiles()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Single(result.PendingChanges);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("get; set;", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("Generate property 'Name'", result.PendingChanges[0].Description);
        Assert.Contains("no property 'Name'", result.PendingChanges[0].BeforeSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Reject Cases

    [SkippableFact]
    public async Task GenerateProperty_NoSymbol_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_UnsupportedTarget_Enum_Throws()
    {
        const string source = """
            namespace TestApp;

            public enum Status
            {
                Ready
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Status",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_NameClash_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingFalse_NameClash_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal("3003", ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void GenerateProperty_UneditableDocument_Throws()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Generated.cs", SourceText.From("class C {}"));

        var ex = Assert.Throws<RefactoringException>(() =>
            GeneratePropertyOperation.ValidateDocumentIsEditable(document, workspace));

        Assert.Equal(ErrorCodes.DocumentNotEditable, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateProperty_MissingField_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                FieldName = "_missing"
            }));

        Assert.Equal(ErrorCodes.SymbolNotFound, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateProperty_ReadonlyField_DefaultSetter_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private readonly string _name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                FieldName = "_name"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("readonly", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReadonlyField_InitOnly_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private readonly string _name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_name",
            InitOnly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("get => _name;", updated);
        Assert.Contains("init => _name = value;", updated);
        Assert.DoesNotContain("set => _name = value;", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_IncompatibleBackingFieldType_Throws()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private int _count;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                FieldName = "_count",
                PropertyType = "string"
            }));

        Assert.Equal(ErrorCodes.InvalidReturnType, ex.ErrorCode);
        Assert.Contains("incompatible", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_CompatibleBackingFieldType_UsesFieldType()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private int _count;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_count",
            PropertyType = "int"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Count", updated);
        Assert.Contains("get => _count;", updated);
        Assert.Contains("set => _count = value;", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_ReadonlyStruct_SettableAutoProperty_Throws()
    {
        const string source = """
            namespace TestApp;

            public readonly struct Point
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                PropertyName = "X",
                PropertyType = "int"
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Contains("readonly struct", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReadonlyStruct_InitOnly_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public readonly struct Point
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            PropertyName = "X",
            PropertyType = "int",
            InitOnly = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int X { get; init; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_StaticField_AddsStaticModifier()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private static string _name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_name"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public static string Name", updated);
        Assert.Contains("get => _name;", updated);
        Assert.Contains("set => _name = value;", updated);
    }

    #endregion

    #region replaceExisting

    private const string WidgetWithNamePropertySource = """
        namespace TestApp;

        public class Widget
        {
            public string Name { get; set; } = "old";
        }
        """;

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_ReplacesAutoProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithNamePropertySource);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public int Name { get; set; }", updated);
        Assert.DoesNotContain("old", updated);
        Assert.DoesNotContain("public string Name", updated);
        Assert.Equal(1, CountOccurrences(updated, "public int Name"));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_InitOnly_ReplacesWithGetInit()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithNamePropertySource);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string",
            InitOnly = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Name { get; init; }", updated);
        Assert.DoesNotContain("{ get; set; }", updated);
        Assert.DoesNotContain("old", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_FieldName_ReplacesPropertyAndKeepsField()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                private string _name = "keep";

                public string Name { get; set; } = "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            FieldName = "_name",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("private string _name = \"keep\";", updated);
        Assert.Contains("get => _name;", updated);
        Assert.Contains("set => _name = value;", updated);
        Assert.DoesNotContain("old", updated);
        Assert.DoesNotContain("{ get; set; }", updated);
        Assert.Equal(1, CountOccurrences(updated, "public string Name"));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_NoExistingProperty_GeneratesAsToday()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public string Name { get; set; }", updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_ExistingFieldSameName_FailsAndLeavesField()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_ExistingMethodSameName_FailsAndLeavesMethod()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public void Name() { }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Contains("public void Name()", before);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_TwoSameNamedProperties_FailsBeforeWrite()
    {
        const string source = """
            namespace TestApp;

            public interface INamed
            {
                string Name { get; set; }
            }

            public class Widget : INamed
            {
                string INamed.Name { get; set; } = "iface";

                public string Name { get; set; } = "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal("3003", ex.ErrorCode);
        Assert.Contains("Multiple properties named 'Name'", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithNamePropertySource);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Single(result.PendingChanges);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Replace property 'Name'", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing property 'Name'", result.PendingChanges[0].BeforeSnippet);
        Assert.Contains("public int Name { get; set; }", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_Preview_NoExisting_IsSingleGenerateChange()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "string",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Single(result.PendingChanges);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Generate property 'Name'", result.PendingChanges[0].Description);
        Assert.Contains("no property 'Name'", result.PendingChanges[0].BeforeSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_PartialOtherFile_RemovesThere_InsertsOnTarget()
    {
        const string typePart = """
            namespace TestApp;

            public partial class Widget
            {
            }
            """;

        const string propertyPart = """
            namespace TestApp;

            public partial class Widget
            {
                public string Name { get; set; } = "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", typePart),
            ("Widget.Properties.cs", propertyPart));
        var otherPath = workspace.PathFor("Widget.Properties.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        Assert.Contains("public int Name { get; set; }", selected);
        Assert.DoesNotContain("old", selected);
        Assert.DoesNotContain("public string Name", other);
        Assert.DoesNotContain("old", other);
        Assert.Equal(1, CountOccurrences(selected, "public int Name"));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string typePart = """
            namespace TestApp;

            public partial class Widget
            {
            }
            """;

        const string propertyPart = """
            namespace TestApp;

            public partial class Widget
            {
                public string Name { get; set; } = "old";
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", typePart),
            ("Widget.Properties.cs", propertyPart));
        var otherPath = workspace.PathFor("Widget.Properties.cs");
        var operation = new GeneratePropertyOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Replace property 'Name'", result.PendingChanges[0].Description);
        var otherChange = result.PendingChanges[1];
        Assert.Equal(otherPath, otherChange.File);
        Assert.Equal(ChangeKind.Modify, otherChange.ChangeType);
        Assert.Contains("Remove existing property 'Name'", otherChange.Description);
        Assert.Contains("public string Name { get; set; }", otherChange.BeforeSnippet);
        Assert.Contains("old", otherChange.BeforeSnippet);
        Assert.Equal("// property removed", otherChange.AfterSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_IfDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            #if DEBUG
                public string Name { get; set; } = "old";
            #endif

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#if DEBUG", updated);
        Assert.Contains("#endif", updated);
        Assert.Contains("public int Name { get; set; }", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old", updated);
        Assert.Equal(updated.Split("#if ").Length - 1, updated.Split("#endif").Length - 1);
        AssertCompiles(updated, preprocessorSymbols: "DEBUG");
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_RegionDirective_PreservesDirectives()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
            #region Name
                public string Name { get; set; } = "old";
            #endregion

                public int Age { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GeneratePropertyParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            PropertyName = "Name",
            PropertyType = "int",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("#region Name", updated);
        Assert.Contains("#endregion", updated);
        Assert.Contains("public int Name { get; set; }", updated);
        Assert.Contains("public int Age { get; set; }", updated);
        Assert.DoesNotContain("old", updated);
        Assert.Equal(updated.Split("#region ").Length - 1, updated.Split("#endregion").Length - 1);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateProperty_ReplaceExistingTrue_RecordPositionalProperty_FailsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public record Widget(string Name);
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source);
        var operation = new GeneratePropertyOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GeneratePropertyParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                PropertyName = "Name",
                PropertyType = "string",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.NameCollision, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region Helpers

    private static INamedTypeSymbol CompileType(string source, string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
                "GeneratePropertyResolveTest",
                new[] { tree },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var decl = tree.GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .First(t => t.Identifier.Text == typeName);
        return model.GetDeclaredSymbol(decl) as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Could not resolve type '{typeName}'.");
    }

    private static void AssertCompiles(string source, params string[] preprocessorSymbols)
    {
        var parseOptions = CSharpParseOptions.Default.WithPreprocessorSymbols(preprocessorSymbols);
        var compilation = CSharpCompilation.Create(
                "GeneratePropertyCompileTest",
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Replaced source did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGeneratePropertyMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasProperty(TypeDeclarationSyntax type, string propertyName) =>
        type.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == propertyName);

    private static string ExtractPropertyFromType(TypeDeclarationSyntax type, string propertyName)
    {
        var property = type.Members.OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.Text == propertyName);
        return NormalizeNewlines(property.ToFullString());
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

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

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

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateProperty_" + Guid.NewGuid().ToString("N"));
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

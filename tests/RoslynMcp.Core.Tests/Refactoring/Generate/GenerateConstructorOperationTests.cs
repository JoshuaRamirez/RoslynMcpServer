using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Generate;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Generate;

/// <summary>
/// Unit tests for GenerateConstructorOperation semantic validation,
/// plus operation-level tests for optional <c>line</c> / <c>column</c>,
/// <c>includeProperties</c>,
/// <c>includeInheritedMembers</c>, <c>replaceExisting</c>,
/// <c>visibility</c>, <c>copyConstructor</c>, <c>classBaseCopy</c>, and <c>callBase</c>.
/// Tests validate type-level constraints for constructor generation.
/// </summary>
public class GenerateConstructorOperationTests
{
    private const string WidgetWithFieldAndPropertySource = """
        namespace TestApp;

        public class Widget
        {
            public string _id;

            public string Name { get; set; }
        }
        """;

    private const string PersonPropertiesOnlySource = """
        namespace TestApp;

        public class Person
        {
            public string Name { get; set; }

            public int Age { get; set; }
        }
        """;

    #region P0 optional line disambiguation

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new GenerateConstructorParams
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
            GenerateConstructorOperation.Validate(new GenerateConstructorParams
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
            GenerateConstructorOperation.Validate(new GenerateConstructorParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    private const string NestedSameNameWidgetSource = """
        namespace TestApp;

        public /* outer-widget */ class Widget
        {
            public string Name { get; set; }

            public /* nested-widget */ class Widget
            {
                public int Age { get; set; }
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateConstructor_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasConstructor(types[0]));
        Assert.False(TypeHasConstructor(types[1]));
        var outerCtor = ExtractConstructorFromType(types[0]);
        Assert.Contains("Name", outerCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", outerCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasConstructor(types[0]));
        Assert.True(TypeHasConstructor(types[1]));
        var nestedCtor = ExtractConstructorFromType(types[1]);
        Assert.Contains("Age", nestedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", nestedCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "outer-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasConstructor(types[0]));
        Assert.False(TypeHasConstructor(types[1]));
        var outerCtor = ExtractConstructorFromType(types[0]);
        Assert.Contains("Name", outerCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", outerCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_Line_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
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
        Assert.Contains("Generate constructor", result.PendingChanges[0].Description);
        Assert.Contains("Widget", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("public Widget(", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("this.Age = age", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("Name", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(root, "Widget", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(
            root, "Widget", FindLine(NestedSameNameWidgetSource, "nested-widget"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(
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
                public string Name { get; set; }

                public class Widget // nested-widget
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = GenerateConstructorOperation.FindTypeDeclaration(root, "Widget", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(root, "Widget", line: 1);

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

        Assert.True(GenerateConstructorOperation.SpanCoversLine(span, 1));
        Assert.True(GenerateConstructorOperation.SpanCoversLine(span, 2));
        Assert.False(GenerateConstructorOperation.SpanCoversLine(span, 3));
        Assert.False(GenerateConstructorOperation.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task GenerateConstructor_LineOnLaterSameFilePartial_ReplaceExisting_InsertsOnSelectedPartial()
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
                    public string Name { get; set; }

                    public Widget(string name)
                    {
                        Name = "old"; /* old-ctor */
                    }
                }

                public /* later-partial */ partial class Widget
                {
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(source, "later-partial"),
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(3, types.Count);
        Assert.False(TypeHasConstructor(types[0]));
        Assert.False(TypeHasConstructor(types[1]));
        Assert.True(TypeHasConstructor(types[2]));
        var selectedCtor = ExtractConstructorFromType(types[2]);
        Assert.Contains("this.Name = name", selectedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("old-ctor", selectedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", selectedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Title", selectedCtor, StringComparison.Ordinal);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(1, CountOccurrences(updated, "public Widget("));
        Assert.DoesNotContain("old-ctor", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_SequentialReplaceExisting_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public string Name { get; set; }

                public Alpha(string name)
                {
                    Name = "old-alpha";
                }
            }

            public class Beta
            {
                public string Title { get; set; }

                public Beta(string title)
                {
                    Title = "old-beta";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new GenerateConstructorParams
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
        Assert.True(TypeHasConstructor(alpha));
        Assert.True(TypeHasConstructor(beta));
        var alphaCtor = ExtractConstructorFromType(alpha);
        var betaCtor = ExtractConstructorFromType(beta);
        Assert.Contains("this.Name = name", alphaCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Title", alphaCtor, StringComparison.Ordinal);
        Assert.Contains("this.Title = title", betaCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", betaCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("old-alpha", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("old-beta", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(alpha.ToFullString()), "public Alpha("));
        Assert.Equal(1, CountOccurrences(NormalizeNewlines(beta.ToFullString()), "public Beta("));
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedWidgetSource = """
        namespace TestApp;

        public class Widget { public string Name { get; set; } public class Widget { public int Age { get; set; } } }
        """;

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new GenerateConstructorParams
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
            GenerateConstructorOperation.Validate(new GenerateConstructorParams
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
            GenerateConstructorOperation.Validate(new GenerateConstructorParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Widget",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [SkippableFact]
    public async Task GenerateConstructor_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasConstructor(types[0]));
        Assert.False(TypeHasConstructor(types[1]));
        var outerCtor = ExtractConstructorFromType(types[0]);
        Assert.Contains("Name", outerCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", outerCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(NestedSameNameWidgetSource, "nested-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasConstructor(types[0]));
        Assert.True(TypeHasConstructor(types[1]));
        var nestedCtor = ExtractConstructorFromType(types[1]);
        Assert.Contains("Age", nestedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", nestedCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget { public string Name");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget { public int Age");

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.False(TypeHasConstructor(types[0]));
        Assert.True(TypeHasConstructor(types[1]));
        var nestedCtor = ExtractConstructorFromType(types[1]);
        Assert.Contains("Age", nestedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", nestedCtor, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget { public string Name");
        var column = ColumnOf(SameLineNestedWidgetSource, "Widget { public string Name");

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasConstructor(types[0]));
        Assert.False(TypeHasConstructor(types[1]));
        var outerCtor = ExtractConstructorFromType(types[0]);
        Assert.Contains("Name", outerCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", outerCtor, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(root, "Widget", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget { public string Name");
        var found = GenerateConstructorOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Widget");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget { public string Name");
        var found = GenerateConstructorOperation.FindTypeDeclaration(
            root, "Widget", line, ColumnOf(SameLineNestedWidgetSource, "Widget { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedWidgetSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedWidgetSource, "Widget { public int Age");
        var found = GenerateConstructorOperation.FindTypeDeclaration(
            root, "Widget", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Widget // split-widget
            {
                public string Name { get; set; }

                public class Widget // nested-widget
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var found = GenerateConstructorOperation.FindTypeDeclaration(
            root, "Widget", identifierLine, ColumnOf(source, "Widget // split-widget"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Widget // split-widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Widget");
        var identifierLine = FindLine(source, "split-widget");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = identifierLine,
            Column = ColumnOf(source, "Widget // split-widget")
        });

        Assert.True(result.Success);
        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Single(types);
        Assert.True(TypeHasConstructor(types[0]));
        var ctor = ExtractConstructorFromType(types[0]);
        Assert.Contains("Name", ctor, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNameWidgetSource).GetRoot();
        var found = GenerateConstructorOperation.FindTypeDeclaration(root, "Widget", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNameWidgetSource, "Widget.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
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
    public async Task GenerateConstructor_Column_Preview_WritesNothing_AndDescribesGeneration()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedWidgetSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedWidgetSource, "public class Widget { public string Name");

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(SameLineNestedWidgetSource, "Widget { public int Age"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Generate constructor", result.PendingChanges[0].Description);
        Assert.Contains("Widget", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.Contains("public Widget(", result.PendingChanges[0].AfterSnippet);
        Assert.Contains("this.Age = age", result.PendingChanges[0].AfterSnippet);
        Assert.DoesNotContain("Name", result.PendingChanges[0].AfterSnippet);
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

        Assert.True(GenerateConstructorOperation.SpanCoversColumn(span, line, startCol));
        Assert.True(GenerateConstructorOperation.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(GenerateConstructorOperation.SpanCoversColumn(span, line, endCol));
        Assert.False(GenerateConstructorOperation.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task GenerateConstructor_SequentialColumn_ReusedWorkspace_InsertsOnSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Widget { public string Name { get; set; } public Widget(string name) { Name = "old"; /* old-outer */ } public class Widget { public int Age { get; set; } public Widget(int age) { Age = 0; /* old-nested */ } } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var line = FindLine(source, "public class Widget { public string Name");

        var first = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = line,
            Column = ColumnOf(source, "Widget { public string Name"),
            ReplaceExisting = true
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Line = FindLine(afterFirst, "old-nested"),
            Column = ColumnOf(afterFirst, "Widget { public int Age"),
            ReplaceExisting = true
        });
        Assert.True(second.Success);

        var types = GetTypes(await File.ReadAllTextAsync(workspace.SourcePath), "Widget");
        Assert.Equal(2, types.Count);
        Assert.True(TypeHasConstructor(types[0]));
        Assert.True(TypeHasConstructor(types[1]));
        var outerCtor = ExtractConstructorFromType(types[0]);
        var nestedCtor = ExtractConstructorFromType(types[1]);
        Assert.Contains("this.Name = name", outerCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Age", outerCtor, StringComparison.Ordinal);
        Assert.Contains("this.Age = age", nestedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("Name", nestedCtor, StringComparison.Ordinal);
        Assert.DoesNotContain("old-outer", types[0].ToFullString(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-nested", types[1].ToFullString(), StringComparison.Ordinal);
        Assert.Single(types[0].Members.OfType<ConstructorDeclarationSyntax>());
        Assert.Single(types[1].Members.OfType<ConstructorDeclarationSyntax>());
    }

    #endregion

    #region Static Class Tests

    [Fact]
    public void GenerateConstructor_StaticClass_ThrowsTypeIsStatic()
    {
        // Arrange
        var typeSymbol = CreateStaticClassSymbol();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateTypeForConstructor(typeSymbol));

        // Assert
        Assert.Equal(ErrorCodes.TypeIsStatic, exception.ErrorCode);
    }

    [Fact]
    public void GenerateConstructor_StaticClass_MessageIndicatesStaticClass()
    {
        // Arrange
        var typeSymbol = CreateStaticClassSymbol();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateTypeForConstructor(typeSymbol));

        // Assert
        Assert.Contains("static class", exception.Message);
    }

    [Fact]
    public void GenerateConstructor_NonStaticClass_DoesNotThrow()
    {
        // Arrange
        var typeSymbol = CreateNonStaticClassSymbol();

        // Act
        var exception = Record.Exception(() => ValidateTypeForConstructor(typeSymbol));

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region No Members Tests

    [Fact]
    public void GenerateConstructor_NoMembers_ThrowsMemberNotFound()
    {
        // Arrange
        var members = new List<ISymbol>();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateMembersForConstructor(members));

        // Assert
        Assert.Equal(ErrorCodes.MemberNotFound, exception.ErrorCode);
    }

    [Fact]
    public void GenerateConstructor_NoMembers_MessageIndicatesNoMembers()
    {
        // Arrange
        var members = new List<ISymbol>();

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateMembersForConstructor(members));

        // Assert
        Assert.Contains("No members", exception.Message);
    }

    [Fact]
    public void GenerateConstructor_RequestedMemberNotFound_ThrowsMemberNotFound()
    {
        // Arrange
        var requestedMembers = new List<string> { "NonExistentField" };
        var availableMembers = new List<string> { "ExistingField" };

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRequestedMembers(requestedMembers, availableMembers));

        // Assert
        Assert.Equal(ErrorCodes.MemberNotFound, exception.ErrorCode);
    }

    [Fact]
    public void GenerateConstructor_RequestedMemberNotFound_MessageListsMissing()
    {
        // Arrange
        var requestedMembers = new List<string> { "NonExistentField" };
        var availableMembers = new List<string> { "ExistingField" };

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateRequestedMembers(requestedMembers, availableMembers));

        // Assert
        Assert.Contains("NonExistentField", exception.Message);
    }

    #endregion

    #region Duplicate Signature Tests

    [Fact]
    public void GenerateConstructor_DuplicateSignature_ThrowsConstructorExists()
    {
        // Arrange
        var existingSignatures = new List<List<string>> { new() { "string", "int" } };
        var newSignature = new List<string> { "string", "int" };

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateConstructorSignature(existingSignatures, newSignature));

        // Assert
        Assert.Equal(ErrorCodes.ConstructorExists, exception.ErrorCode);
    }

    [Fact]
    public void GenerateConstructor_DuplicateSignature_MessageIndicatesExists()
    {
        // Arrange
        var existingSignatures = new List<List<string>> { new() { "string", "int" } };
        var newSignature = new List<string> { "string", "int" };

        // Act
        var exception = Assert.Throws<RefactoringException>(() =>
            ValidateConstructorSignature(existingSignatures, newSignature));

        // Assert
        Assert.Contains("already exists", exception.Message);
    }

    [Fact]
    public void GenerateConstructor_DifferentSignature_DoesNotThrow()
    {
        // Arrange
        var existingSignatures = new List<List<string>> { new() { "string" } };
        var newSignature = new List<string> { "string", "int" };

        // Act
        var exception = Record.Exception(() =>
            ValidateConstructorSignature(existingSignatures, newSignature));

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region Null Checks Tests

    [Fact]
    public void GenerateConstructor_WithNullChecks_GeneratesArgumentNullException()
    {
        // Arrange
        var addNullChecks = true;
        var memberType = "string"; // reference type

        // Act
        var nullCheckStatement = GenerateNullCheck("name", memberType, addNullChecks);

        // Assert
        Assert.Contains("ArgumentNullException", nullCheckStatement);
    }

    [Fact]
    public void GenerateConstructor_WithNullChecks_UsesNameof()
    {
        // Arrange
        var addNullChecks = true;
        var memberType = "string";

        // Act
        var nullCheckStatement = GenerateNullCheck("name", memberType, addNullChecks);

        // Assert
        Assert.Contains("nameof", nullCheckStatement);
    }

    [Fact]
    public void GenerateConstructor_WithoutNullChecks_GeneratesNoNullCheck()
    {
        // Arrange
        var addNullChecks = false;
        var memberType = "string";

        // Act
        var nullCheckStatement = GenerateNullCheck("name", memberType, addNullChecks);

        // Assert
        Assert.Empty(nullCheckStatement);
    }

    [Fact]
    public void GenerateConstructor_ValueType_NoNullCheckEvenIfRequested()
    {
        // Arrange
        var addNullChecks = true;
        var memberType = "int"; // value type

        // Act
        var nullCheckStatement = GenerateNullCheck("id", memberType, addNullChecks);

        // Assert
        Assert.Empty(nullCheckStatement);
    }

    #endregion

    #region Camel Case Parameter Generation Tests

    [Fact]
    public void GenerateConstructor_UnderscorePrefixField_GeneratesCamelCaseParam()
    {
        // Arrange
        var fieldName = "_userName";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("userName", paramName);
    }

    [Fact]
    public void GenerateConstructor_PascalCaseField_GeneratesCamelCaseParam()
    {
        // Arrange
        var fieldName = "UserName";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("userName", paramName);
    }

    [Fact]
    public void GenerateConstructor_AllCapsField_GeneratesLowercaseFirstChar()
    {
        // Arrange
        var fieldName = "ID";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("iD", paramName);
    }

    [Fact]
    public void GenerateConstructor_AlreadyCamelCase_RemainsUnchanged()
    {
        // Arrange
        var fieldName = "userName";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("userName", paramName);
    }

    [Fact]
    public void GenerateConstructor_DoubleUnderscorePrefix_RemovesFirstUnderscore()
    {
        // Arrange
        var fieldName = "__value";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("_value", paramName);
    }

    [Fact]
    public void GenerateConstructor_SingleCharField_GeneratesLowercase()
    {
        // Arrange
        var fieldName = "X";

        // Act
        var paramName = ToCamelCase(fieldName);

        // Assert
        Assert.Equal("x", paramName);
    }

    #endregion

    #region includeProperties

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesOmitted_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesTrue_IncludesFieldAndProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.DoesNotContain("Name", ctor);
        Assert.DoesNotContain("name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_EmptyMembersList_IncludesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Members = Array.Empty<string>(),
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.DoesNotContain("Name", ctor);
        Assert.DoesNotContain("name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_PropertiesOnly_FailsWithMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PersonPropertiesOnlySource);
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("No members", ex.Message);
        Assert.Equal(PersonPropertiesOnlySource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_MembersNamesProperty_IncludesThatProperty()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Members = new[] { "Name" },
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("_id", ctor);
        Assert.DoesNotContain("string id", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_AddNullChecks_StillAppliesToFields()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("ArgumentNullException", ctor);
        Assert.Contains("nameof(id)", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.DoesNotContain("Name", ctor);
        Assert.DoesNotContain("nameof(name)", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_Preview_DoesNotWriteFiles_AndDescribesFieldOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeProperties = false,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("_id = id", snippet);
        Assert.DoesNotContain("Name", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludePropertiesFalse_DuplicateFieldCtor_StillRejected()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }

                public Widget(string id)
                {
                    _id = id;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region includeInheritedMembers

    private const string AnimalSource = """
        namespace TestApp;

        public class Animal
        {
            public string Species;

            protected int Legs;

            private string Secret;

            public readonly string Immutable;

            public string Nickname { get; set; }

            public string ReadOnlyName { get; }
        }
        """;

    private const string DogSource = """
        namespace TestApp;

        public class Dog : Animal
        {
            public string Name;
        }
        """;

    private static Task<TempWorkspace> CreateDogOnAnimalAsync() =>
        TempWorkspace.CreateAsync(("Dog.cs", DogSource), ("Animal.cs", AnimalSource));

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersOmitted_DoesNotInitializeBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Species", ctor);
        Assert.DoesNotContain("Legs", ctor);
        Assert.DoesNotContain("Secret", ctor);
        Assert.DoesNotContain("Nickname", ctor);
        Assert.DoesNotContain("Immutable", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersFalse_DoesNotInitializeBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Species", ctor);
        Assert.DoesNotContain("Legs", ctor);
        Assert.DoesNotContain("Nickname", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_IncludesPublicAndProtectedBaseFieldsAndSettableProperties()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Species = species", ctor);
        Assert.Contains("this.Legs = legs", ctor);
        Assert.Contains("this.Nickname = nickname", ctor);
        Assert.DoesNotContain("Secret", ctor);
        Assert.DoesNotContain("Immutable", ctor);
        Assert.DoesNotContain("ReadOnlyName", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_SkipsPrivateBaseField()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.DoesNotContain("Secret", ctor);
        Assert.DoesNotContain("secret", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_IncludePropertiesFalse_SkipsInheritedProperties()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Species = species", ctor);
        Assert.Contains("this.Legs = legs", ctor);
        Assert.DoesNotContain("Nickname", ctor);
        Assert.DoesNotContain("nickname", ctor);
        Assert.DoesNotContain("Secret", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_MembersNamesInheritedMember_IncludesIt()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            IncludeProperties = false,
            Members = new[] { "Nickname" }
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Nickname = nickname", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.DoesNotContain("Species", ctor);
        Assert.DoesNotContain("Legs", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersFalse_MembersNamesInheritedMember_NotFound()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = false,
                Members = new[] { "Species" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("Species", ex.Message);
        Assert.Equal(DogSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_ObjectOnlyBase_NoExtraMembers()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Equals", ctor);
        Assert.DoesNotContain("GetHashCode", ctor);
        Assert.DoesNotContain("GetType", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_Preview_DoesNotWriteFiles_AndDescribesInherited()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("inherited members", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("Species", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("this.Name = name", snippet);
        Assert.Contains("this.Species = species", snippet);
        Assert.Contains("this.Legs = legs", snippet);
        Assert.Contains("this.Nickname = nickname", snippet);
        Assert.DoesNotContain("Secret", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_Override_InitializesDerivedPropertyOnce()
    {
        const string source = """
            namespace TestApp;

            public class NamedBase
            {
                public virtual string Title { get; set; }
            }

            public class NamedOverride : NamedBase
            {
                public override string Title { get; set; }

                public string Extra;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "NamedOverride.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "NamedOverride",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "NamedOverride");
        Assert.Contains("this.Extra = extra", ctor);
        Assert.Contains("this.Title = title", ctor);
        Assert.Equal(1, CountOccurrences(ctor, "this.Title"));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_CloserMethodHidesInheritedField()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Name;

                public string Species;
            }

            public class Dog : Animal
            {
                public string Extra;

                public string Name() => Extra;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Extra = extra", ctor);
        Assert.Contains("this.Species = species", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.DoesNotContain("string name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_AddNullChecks_AppliesToInheritedReferenceFields()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("nameof(name)", ctor);
        Assert.Contains("nameof(species)", ctor);
        Assert.Contains("nameof(nickname)", ctor);
        Assert.DoesNotContain("nameof(legs)", ctor);
        Assert.Contains("this.Legs = legs", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_DuplicateInheritedSignature_StillRejected()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;

                public Dog(string name, string species)
                {
                    Name = name;
                    Species = species;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = true,
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ThisTypeWritableIndexer_IsNotCollected()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public string Name;

                public string this[int index]
                {
                    get => Name;
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Lookup");
        Assert.Contains("this.Name = name", ctor);
        AssertNoIndexerAssignment(ctor);
        Assert.DoesNotContain("this[", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_SkipsBaseWritableIndexer()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;

                public string this[int index]
                {
                    get => Species;
                    set { }
                }
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Species = species", ctor);
        AssertNoIndexerAssignment(ctor);
        Assert.DoesNotContain("this[", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_MembersNamesIndexer_DoesNotEmitIndexerAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public string Name;

                public string this[int index]
                {
                    get => Name;
                    set { }
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        foreach (var indexerName in new[] { "this[]", "Item" })
        {
            var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
                operation.ExecuteAsync(new GenerateConstructorParams
                {
                    SourceFile = workspace.SourcePath,
                    TypeName = "Lookup",
                    Members = new[] { indexerName }
                }));

            Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
            Assert.Contains(indexerName, ex.Message);
        }

        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_IncludeInheritedMembersTrue_MembersNamesBaseIndexer_DoesNotEmitIndexerAssignment()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;

                public string this[int index]
                {
                    get => Species;
                    set { }
                }
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = true,
                Members = new[] { "this[]" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("this[]", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region replaceExisting

    private const string WidgetWithExactCtorSource = """
        namespace TestApp;

        public class Widget
        {
            public string _id;

            public string Name { get; set; }

            public Widget(string id, string name)
            {
                _id = "old";
                Name = "old";
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingOmitted_ExactSignature_FailsWithConstructorExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget"
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(WidgetWithExactCtorSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingFalse_ExactSignature_FailsWithConstructorExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(WidgetWithExactCtorSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_ExactSignature_ReplacesConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget");
        Assert.DoesNotContain("\"old\"", updated);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Equal(1, CountOccurrences(updated, "public Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_NoExistingConstructor_GeneratesAsToday()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_OptionalParamAmbiguity_StillRejected()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name;

                public Widget(string name, int extra = 0)
                {
                    Name = name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("optional parameters", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_RequiredParamConflict_StillRejected()
    {
        // Existing optional-param ctor has the same required prefix as the generated
        // signature (Widget(string)) but a different full signature (string, int?).
        // That is not an exact match, so replaceExisting must not pick an overload.
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name;

                public Widget(string name, int? unused = null)
                {
                    Name = name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.DoesNotContain("already exists", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_DifferentSignature_LeavesExistingAndGenerates()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name;

                public int Age;

                public Widget(string name)
                {
                    Name = name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public Widget(string name)", updated);
        Assert.Contains("Name = name;", updated);
        Assert.Contains("this.Name = name", updated);
        Assert.Contains("this.Age = age", updated);
        Assert.Equal(2, CountOccurrences(updated, "public Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_PartialOtherFile_RemovesOtherPartConstructor()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Widget
            {
                public string _id;

                public string Name { get; set; }
            }
            """;

        const string ctorPart = """
            namespace TestApp;

            public partial class Widget
            {
                public Widget(string id, string name)
                {
                    _id = "old";
                    Name = "old";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", fieldsPart),
            ("Widget.Ctor.cs", ctorPart));
        var otherPath = workspace.PathFor("Widget.Ctor.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var selected = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var other = NormalizeNewlines(await File.ReadAllTextAsync(otherPath));
        var ctor = ExtractConstructor(selected, "Widget");
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("\"old\"", selected);
        Assert.DoesNotContain("public Widget(", other);
        Assert.DoesNotContain("\"old\"", other);
        Assert.Equal(1, CountOccurrences(selected, "public Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_Preview_DoesNotWriteFiles_AndDescribesReplacement()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Replace constructor", result.PendingChanges[0].Description);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        Assert.Contains("replacing existing constructor", result.PendingChanges[0].BeforeSnippet);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("_id = id", snippet);
        Assert.Contains("this.Name = name", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_PartialOtherFile_Preview_DoesNotWriteFiles()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Widget
            {
                public string _id;

                public string Name { get; set; }
            }
            """;

        const string ctorPart = """
            namespace TestApp;

            public partial class Widget
            {
                public Widget(string id, string name)
                {
                    _id = "old";
                    Name = "old";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", fieldsPart),
            ("Widget.Ctor.cs", ctorPart));
        var otherPath = workspace.PathFor("Widget.Ctor.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("Replace constructor", result.PendingChanges![0].Description);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        var otherChange = Assert.Single(
            result.PendingChanges,
            c => !string.Equals(c.File, workspace.SourcePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(otherPath, otherChange.File);
        Assert.Equal(RoslynMcp.Contracts.Enums.ChangeKind.Modify, otherChange.ChangeType);
        Assert.Contains("Remove existing constructor", otherChange.Description);
        Assert.Contains("public Widget(string id, string name)", otherChange.BeforeSnippet);
        Assert.Contains("_id = \"old\"", otherChange.BeforeSnippet);
        Assert.Contains("constructor removed", otherChange.AfterSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_IncludePropertiesFalse_AddNullChecks_StillWorks()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string _id;

                public string Name { get; set; }

                public Widget(string id)
                {
                    _id = "old";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true,
            IncludeProperties = false,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget");
        Assert.DoesNotContain("\"old\"", updated);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("nameof(id)", ctor);
        Assert.DoesNotContain("this.Name", ctor);
        Assert.DoesNotContain("string name", ctor);
        Assert.Equal(1, CountOccurrences(updated, "public Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_IncludeInheritedMembers_StillWorks()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species;
            }

            public class Dog : Animal
            {
                public string Name;

                public Dog(string name, string species)
                {
                    Name = "old";
                    Species = "old";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            ReplaceExisting = true,
            IncludeInheritedMembers = true,
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Dog");
        Assert.DoesNotContain("\"old\"", updated);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Species = species", ctor);
        Assert.Equal(1, CountOccurrences(updated, "public Dog("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_MemberNotFound_Unchanged()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = true,
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("DoesNotExist", ex.Message);
        Assert.Equal(WidgetWithExactCtorSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_ClassPrimaryConstructor_FailsWithConstructorExists()
    {
        const string source = """
            namespace TestApp;

            public class Widget(string id)
            {
                public string _id = id;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = true,
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("public Widget(string id)", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_RecordPrimaryConstructor_FailsWithConstructorExists()
    {
        const string source = """
            namespace TestApp;

            public record Widget(string id)
            {
                public string _id = id;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                ReplaceExisting = true,
                IncludeProperties = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(source, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public record Widget(string id)", await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.DoesNotContain("this._id", await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_ExistingRefCtor_DoesNotRemoveRefCtor()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name;

                public Widget(ref string name)
                {
                    Name = name;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public Widget(ref string name)", updated);
        Assert.Contains("Name = name;", updated);
        Assert.Contains("this.Name = name", updated);
        Assert.Equal(2, CountOccurrences(updated, "public Widget("));
        Assert.Equal(1, CountOccurrences(updated, "ref string name"));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_PartialOtherFile_Preview_IncludesOtherPartialBeforeSnippet()
    {
        const string fieldsPart = """
            namespace TestApp;

            public partial class Widget
            {
                public string _id;

                public string Name { get; set; }
            }
            """;

        const string ctorPart = """
            namespace TestApp;

            public partial class Widget
            {
                public Widget(string id, string name)
                {
                    _id = "old";
                    Name = "old";
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(
            ("Widget.cs", fieldsPart),
            ("Widget.Ctor.cs", ctorPart));
        var otherPath = workspace.PathFor("Widget.Ctor.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var beforeSelected = await File.ReadAllTextAsync(workspace.SourcePath);
        var beforeOther = await File.ReadAllTextAsync(otherPath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Equal(2, result.PendingChanges.Count);
        Assert.Equal(workspace.SourcePath, result.PendingChanges[0].File);
        Assert.Contains("Replace constructor", result.PendingChanges[0].Description);
        Assert.Contains("_id = id", result.PendingChanges[0].AfterSnippet);
        var otherChange = result.PendingChanges[1];
        Assert.Equal(otherPath, otherChange.File);
        Assert.Contains("Remove existing constructor", otherChange.Description);
        Assert.Contains("public Widget(string id, string name)", otherChange.BeforeSnippet);
        Assert.Contains("_id = \"old\"", otherChange.BeforeSnippet);
        Assert.Contains("Name = \"old\"", otherChange.BeforeSnippet);
        Assert.Equal(beforeSelected, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(beforeOther, await File.ReadAllTextAsync(otherPath));
    }

    #endregion

    #region visibility

    [SkippableFact]
    public async Task GenerateConstructor_VisibilityOmitted_EmitsPublicConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget");
        Assert.StartsWith("public Widget(", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("private Widget(", updated);
        Assert.DoesNotContain("internal Widget(", updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_VisibilityPublic_EmitsPublicConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Visibility = "public"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(
            NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.StartsWith("public Widget(", ctor);
    }

    [SkippableTheory]
    [InlineData("private")]
    [InlineData("protected")]
    [InlineData("internal")]
    [InlineData("protected internal")]
    [InlineData("private protected")]
    [InlineData("Internal")]
    public async Task GenerateConstructor_ValidVisibility_EmitsThatModifier(string visibility)
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Visibility = visibility
        });

        Assert.True(result.Success);
        var expected = visibility.ToLowerInvariant();
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget", expected);
        Assert.StartsWith($"{expected} Widget(", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        if (!expected.Contains("public", StringComparison.Ordinal))
            Assert.DoesNotContain("public Widget(", updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_InvalidVisibility_FailsWithInvalidVisibility_AndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                Visibility = "secret"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ReplaceExistingTrue_UsesRequestedVisibility_NotOldAccessibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithExactCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            ReplaceExisting = true,
            Visibility = "internal"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget", "internal");
        Assert.StartsWith("internal Widget(", ctor);
        Assert.DoesNotContain("\"old\"", updated);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("public Widget(", updated);
        Assert.Equal(1, CountOccurrences(updated, "internal Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_VisibilityInternal_IncludePropertiesFalse_AddNullChecks_StillWorks()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Visibility = "private",
            IncludeProperties = false,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget", "private");
        Assert.StartsWith("private Widget(", ctor);
        Assert.Contains("ArgumentNullException", ctor);
        Assert.Contains("nameof(id)", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.DoesNotContain("Name", ctor);
        Assert.DoesNotContain("nameof(name)", ctor);
        Assert.DoesNotContain("public Widget(", updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_VisibilityProtected_Preview_DoesNotWriteFiles_AndDescribesVisibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Visibility = "protected",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("protected", result.PendingChanges[0].Description);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("protected Widget(", snippet);
        Assert.Contains("_id = id", snippet);
        Assert.Contains("this.Name = name", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_VisibilityOmitted_Preview_MentionsPublic()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("public", result.PendingChanges![0].Description);
        Assert.Contains("public Widget(", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    private const string PointStructSource = """
        namespace TestApp;

        public struct Point
        {
            public int X;

            public int Y;
        }
        """;

    private const string PointStructWithCtorSource = """
        namespace TestApp;

        public struct Point
        {
            public int X;

            public int Y;

            public Point(int x, int y)
            {
                X = 0;
                Y = 0;
            }
        }
        """;

    private const string PointRecordStructSource = """
        namespace TestApp;

        public record struct Point
        {
            public int X;

            public int Y;
        }
        """;

    [SkippableTheory]
    [InlineData("protected")]
    [InlineData("protected internal")]
    [InlineData("private protected")]
    public async Task GenerateConstructor_Struct_ProtectedVisibility_FailsWithInvalidVisibility_AndWritesNothing(
        string visibility)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                Visibility = visibility
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Contains("struct", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS0666", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_RecordStruct_Protected_FailsWithInvalidVisibility_AndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointRecordStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                Visibility = "protected"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Contains("struct", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableTheory]
    [InlineData(null, "public")]
    [InlineData("public", "public")]
    [InlineData("internal", "internal")]
    [InlineData("private", "private")]
    public async Task GenerateConstructor_Struct_NonProtectedVisibility_Succeeds(string? visibility, string expected)
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            Visibility = visibility
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(
            NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point", expected);
        Assert.StartsWith($"{expected} Point(", ctor);
        Assert.Contains("this.X = x", ctor);
        Assert.Contains("this.Y = y", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_Class_Protected_StillSucceeds()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            Visibility = "protected"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(
            NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget", "protected");
        Assert.StartsWith("protected Widget(", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_Struct_Protected_Preview_FailsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                Visibility = "protected",
                Preview = true
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_Struct_Protected_ReplaceExisting_FailsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructWithCtorSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                Visibility = "protected internal",
                ReplaceExisting = true
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("public Point(int x, int y)", before);
    }

    #endregion

    #region copyConstructor

    private const string WidgetWithCopyCtorSource = """
        namespace TestApp;

        public class Widget
        {
            public string _id;

            public string Name { get; set; }

            public Widget(Widget other)
            {
                _id = "old";
                Name = "old";
            }
        }
        """;

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorOmitted_KeepsMultiParameterConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("string id", ctor);
        Assert.Contains("string name", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Widget other", ctor);
        Assert.DoesNotContain("other._id", ctor);
        Assert.DoesNotContain("other.Name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorFalse_KeepsMultiParameterConstructor()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("string id", ctor);
        Assert.Contains("string name", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Widget other", ctor);
        Assert.DoesNotContain("other._id", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_GeneratesSingleSameTypeParameter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("string id", ctor);
        Assert.DoesNotContain("string name", ctor);
        Assert.DoesNotContain("_id = id", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_IncludePropertiesFalse_FieldsOnly()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            IncludeProperties = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.DoesNotContain("Name", ctor);
        Assert.DoesNotContain("other.Name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_IncludeInheritedMembersTrue_IncludesAccessibleInherited()
    {
        await using var workspace = await CreateDogOnAnimalAsync();
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.Contains("this.Species = other.Species", ctor);
        Assert.Contains("this.Legs = other.Legs", ctor);
        Assert.Contains("this.Nickname = other.Nickname", ctor);
        Assert.DoesNotContain("Secret", ctor);
        Assert.DoesNotContain("Immutable", ctor);
        Assert.DoesNotContain("ReadOnlyName", ctor);
        Assert.DoesNotContain("string name", ctor);
        Assert.DoesNotContain("string species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_NamedMembers_OnlyThoseAssigned()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            Members = new[] { "Name" }
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("_id", ctor);
        Assert.DoesNotContain("other._id", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_AddNullChecks_OnClass_NullChecksCopyParameter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("ArgumentNullException", ctor);
        Assert.Contains("nameof(other)", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("nameof(id)", ctor);
        Assert.DoesNotContain("nameof(name)", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_Struct_NoNullCheckOnCopyParameter()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            CopyConstructor = true,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point");
        Assert.Contains("public Point(Point other)", ctor);
        Assert.Contains("this.X = other.X", ctor);
        Assert.Contains("this.Y = other.Y", ctor);
        Assert.DoesNotContain("ArgumentNullException", ctor);
        Assert.DoesNotContain("nameof", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_ExistingCopyCtor_ReplaceExistingFalse_ConstructorExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithCopyCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                CopyConstructor = true,
                ReplaceExisting = false
            }));

        Assert.Equal(ErrorCodes.ConstructorExists, ex.ErrorCode);
        Assert.Contains("already exists", ex.Message);
        Assert.Equal(WidgetWithCopyCtorSource, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_ExistingCopyCtor_ReplaceExistingTrue_Replaces()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithCopyCtorSource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            ReplaceExisting = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget");
        Assert.DoesNotContain("\"old\"", updated);
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.Equal(1, CountOccurrences(updated, "public Widget("));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_VisibilityInternal_EmitsInternal()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            Visibility = "internal"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Widget", "internal");
        Assert.StartsWith("internal Widget(", ctor);
        Assert.Contains("internal Widget(Widget other)", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("public Widget(", updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_Struct_Protected_FailsWithInvalidVisibility()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Point",
                CopyConstructor = true,
                Visibility = "protected"
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Contains("struct", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CS0666", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_OtherNameCollision_FallsBackToSource()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string other;

                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget source)", ctor);
        Assert.Contains("this.other = source.other", ctor);
        Assert.Contains("this.Name = source.Name", ctor);
        Assert.DoesNotContain("Widget other", ctor);
        Assert.DoesNotContain("other.Name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_Preview_DoesNotWriteFiles_AndDescribesCopyMode()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("copy constructor", result.PendingChanges[0].Description);
        Assert.Contains("other", result.PendingChanges[0].Description);
        Assert.Contains("_id", result.PendingChanges[0].Description);
        Assert.Contains("Name", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Widget(Widget other)", snippet);
        Assert.Contains("this._id = other._id", snippet);
        Assert.Contains("this.Name = other.Name", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_DerivedRecord_PositionalBase_EmitsBaseCopyInitializer()
    {
        const string source = """
            namespace TestApp;

            public record Animal(string Species);

            public record Dog(string Species, string Name) : Animal(Species);
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other) : base(other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        // Species is declared on the positional base and is copied by : base(other),
        // not reassigned in the derived constructor when includeInheritedMembers is false.
        Assert.DoesNotContain("this.Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_DerivedRecordClass_EmitsBaseCopyInitializer()
    {
        const string source = """
            namespace TestApp;

            public record class Animal
            {
                public string Species { get; set; }
            }

            public record class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other) : base(other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_ClassWithParameterlessBase_HasNoBaseInitializer()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species { get; set; }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableTheory]
    [InlineData("internal")]
    [InlineData("private")]
    [InlineData("protected internal")]
    [InlineData("private protected")]
    public async Task GenerateConstructor_CopyConstructorTrue_UnsealedRecord_InvalidVisibility_FailsWithInvalidVisibility(
        string visibility)
    {
        const string source = """
            namespace TestApp;

            public record Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                CopyConstructor = true,
                Visibility = visibility
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Contains("CS8878", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unsealed record", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_UnsealedRecord_Protected_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public record Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            Visibility = "protected"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(
            NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget", "protected");
        Assert.StartsWith("protected Widget(", ctor);
        Assert.Contains("protected Widget(Widget other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain(": base(", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_SealedRecord_Internal_Succeeds()
    {
        const string source = """
            namespace TestApp;

            public sealed record Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            Visibility = "internal"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(
            NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget", "internal");
        Assert.StartsWith("internal Widget(", ctor);
        Assert.Contains("internal Widget(Widget other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_UnsealedRecord_Internal_Preview_WritesNothing()
    {
        const string source = """
            namespace TestApp;

            public record Widget
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                CopyConstructor = true,
                Visibility = "internal",
                Preview = true
            }));

        Assert.Equal(ErrorCodes.InvalidVisibility, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorFalse_SetterOnlyProperty_StillIncluded()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }

                public int Value { set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("string name", ctor);
        Assert.Contains("int value", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Value = value", ctor);
        Assert.DoesNotContain("other.Value", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_AutoCollection_SkipsSetterOnlyProperty()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }

                public int Value { set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Value", ctor);
        Assert.DoesNotContain("other.Value", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_NamedSetterOnlyProperty_FailsWithMemberNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Widget
            {
                public string Name { get; set; }

                public int Value { set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Widget",
                CopyConstructor = true,
                Members = new[] { "Value" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("Value", ex.Message);
        Assert.Contains("readable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_IncludeInheritedMembers_SkipsInheritedSetterOnlyAndPrivateGetter()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Secret { set { } }

                public string Hidden { private get; set; }

                public string Species { get; set; }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.Contains("this.Species = other.Species", ctor);
        Assert.DoesNotContain("Secret", ctor);
        Assert.DoesNotContain("Hidden", ctor);
        Assert.DoesNotContain("other.Secret", ctor);
        Assert.DoesNotContain("other.Hidden", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CopyConstructorTrue_NamedInheritedUnreadableProperty_FailsWithMemberNotFound()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public int Secret { set { } }

                public string Species { get; set; }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CopyConstructor = true,
                IncludeInheritedMembers = true,
                Members = new[] { "Secret" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Contains("Secret", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region classBaseCopy

    private const string DerivedClassWithBaseCopyCtorSource = """
        namespace TestApp;

        public class Animal
        {
            public string Species { get; set; }

            public Animal()
            {
            }

            public Animal(Animal other)
            {
                Species = other.Species;
            }
        }

        public class Dog : Animal
        {
            public string Name { get; set; }
        }
        """;

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyOmitted_CopyConstructorTrue_DerivedClass_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyFalse_CopyConstructorTrue_DerivedClass_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_DerivedClassWithAccessibleBaseCopyCtor_EmitsBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other) : base((Animal)other)", ctor);
        Assert.DoesNotContain(": base(other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("this.Species", ctor);
        Assert.DoesNotContain("Species = other.Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_AmbiguousBaseOverloads_CastsToBaseType_AndCompiles()
    {
        const string source = """
            namespace TestApp;

            public interface IFoo
            {
            }

            public class Base
            {
                public Base()
                {
                }

                public Base(Base other)
                {
                }

                public Base(IFoo foo)
                {
                }
            }

            public class Derived : Base, IFoo
            {
                public int N;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Derived.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Derived",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Derived");
        Assert.Contains("public Derived(Derived other) : base((Base)other)", ctor);
        Assert.DoesNotContain(": base(other)", ctor);
        Assert.Contains("this.N = other.N", ctor);
        AssertGeneratedSourceCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_IncludeInheritedMembersTrue_DoesNotReassignInherited()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other) : base((Animal)other)", ctor);
        Assert.DoesNotContain(": base(other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("this.Species", ctor);
        Assert.DoesNotContain("Species = other.Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_BaseHasOnlyParameterlessCtor_HasNoBaseInitializer()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species { get; set; }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_ClassInheritingObject_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(Widget other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this._id = other._id", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_CopyConstructorFalse_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CopyConstructor = false,
                ClassBaseCopy = true
            }));

        Assert.Equal(ErrorCodes.ClassBaseCopyRequiresCopyConstructor, ex.ErrorCode);
        Assert.Contains("copyConstructor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_CopyConstructorOmitted_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                ClassBaseCopy = true
            }));

        Assert.Equal(ErrorCodes.ClassBaseCopyRequiresCopyConstructor, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_CopyConstructorFalse_Preview_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CopyConstructor = false,
                ClassBaseCopy = true,
                Preview = true
            }));

        Assert.Equal(ErrorCodes.ClassBaseCopyRequiresCopyConstructor, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_DerivedRecord_RecordChainUnchanged()
    {
        const string source = """
            namespace TestApp;

            public record Animal(string Species);

            public record Dog(string Species, string Name) : Animal(Species);
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other) : base(other)", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("this.Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_Struct_IsNoOp()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point");
        Assert.Contains("public Point(Point other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.X = other.X", ctor);
        Assert.Contains("this.Y = other.Y", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_RecordStruct_IsNoOp()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointRecordStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point");
        Assert.Contains("public Point(Point other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.X = other.X", ctor);
        Assert.Contains("this.Y = other.Y", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_PrivateOnlyBaseCopyCtor_TreatsAsAbsent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species { get; set; }

                public Animal()
                {
                }

                private Animal(Animal other)
                {
                    Species = other.Species;
                }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(Dog other)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Name = other.Name", ctor);
        Assert.DoesNotContain("this.Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_Preview_WritesNothing_AndDescribesInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseCopyCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("copy constructor", result.PendingChanges[0].Description);
        Assert.Contains("class : base(other) initializer", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Dog(Dog other) : base((Animal)other)", snippet);
        Assert.DoesNotContain(": base(other)", snippet);
        Assert.Contains("this.Name = other.Name", snippet);
        Assert.DoesNotContain("this.Species", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_ClassBaseCopyTrue_NoAccessibleBaseCopyCtor_Preview_SaysNoneAdded()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species { get; set; }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CopyConstructor = true,
            ClassBaseCopy = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.Contains("no class base-copy initializer was added", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Dog(Dog other)", snippet);
        Assert.DoesNotContain(": base(", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #endregion

    #region callBase

    private const string DerivedClassWithBaseIntStringCtorSource = """
        namespace TestApp;

        public class Animal
        {
            public Animal(int age, string name)
            {
            }
        }

        public class Dog : Animal
        {
            public int Age;
            public string Name;
            public bool Active;
        }
        """;

    private const string DerivedClassWithInheritedIntStringFieldsSource = """
        namespace TestApp;

        public class Animal
        {
            public int Age;
            public string Label;

            public Animal(int age, string label)
            {
                Age = age;
                Label = label;
            }
        }

        public class Dog : Animal
        {
            public bool Flag;
        }
        """;

    private const string DerivedRecordWithBaseIntStringCtorSource = """
        namespace TestApp;

        public record Animal
        {
            public Animal(int age, string name)
            {
            }
        }

        public record Dog : Animal
        {
            public int Age;
            public string Name;
            public bool Active;
        }
        """;

    private const string DerivedRecordWithInheritedIntStringFieldsSource = """
        namespace TestApp;

        public record Animal
        {
            public int Age;
            public string Label;

            public Animal(int age, string label)
            {
                Age = age;
                Label = label;
            }
        }

        public record Dog : Animal
        {
            public bool Flag;
        }
        """;

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseOmitted_DerivedClass_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name, bool active)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseFalse_DerivedClass_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name, bool active)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_PrefixMatch_EmitsBaseAndDoesNotReassignPassedThrough()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Dog");
        Assert.Contains("public Dog(int age, string name, bool active) : base(age, name)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
        AssertGeneratedSourceCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_AddNullChecks_GuardsForwardedReferenceParams()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(string name)
                {
                }
            }

            public class Dog : Animal
            {
                public string Name;
                public bool Active;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(string name, bool active) : base(name)", ctor);
        Assert.Contains("ArgumentNullException", ctor);
        Assert.Contains("nameof(name)", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
        var nameGuard = ctor.IndexOf("nameof(name)", StringComparison.Ordinal);
        var activeAssign = ctor.IndexOf("this.Active = active", StringComparison.Ordinal);
        Assert.True(nameGuard >= 0 && activeAssign > nameGuard, "Null check for forwarded name must appear before assignments.");
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_IncludeInheritedMembers_DoesNotReassignInherited()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithInheritedIntStringFieldsSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string label, bool flag) : base(age, label)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Label = label", ctor);
        Assert.Contains("this.Flag = flag", ctor);

        await using var withoutCallBase = await TempWorkspace.CreateAsync(DerivedClassWithInheritedIntStringFieldsSource, "Dog.cs");
        var withoutResult = await new GenerateConstructorOperation(withoutCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withoutCallBase.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = true
            });
        Assert.True(withoutResult.Success);
        var withoutCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withoutCallBase.SourcePath)), "Dog");
        Assert.Contains("public Dog(bool flag, int age, string label)", withoutCtor);
        Assert.DoesNotContain(": base(", withoutCtor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_OnlyAccessibleParameterlessBaseCtor_EmitsBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public string Species { get; set; }

                public Animal()
                {
                }
            }

            public class Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(string name) : base()", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_CopyConstructorTrue_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true,
                CopyConstructor = true
            }));

        Assert.Equal(ErrorCodes.CallBaseConflictsWithCopyConstructor, ex.ErrorCode);
        Assert.Contains("copyConstructor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_CopyConstructorTrue_Preview_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true,
                CopyConstructor = true,
                Preview = true
            }));

        Assert.Equal(ErrorCodes.CallBaseConflictsWithCopyConstructor, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_ClassInheritingObject_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(string id, string name)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("_id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_NoMatchingBaseCtorAndNoParameterless_RejectsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int age)
                {
                }
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true
            }));

        Assert.Equal(ErrorCodes.NoMatchingBaseConstructor, ex.ErrorCode);
        Assert.Contains("No accessible constructor", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_TwoEquallyMatchingBaseCtors_RejectsAmbiguityAndWritesNothing()
    {
        // Duplicate signatures (CS0111) still produce two constructor symbols so
        // the name-match tie / ambiguity reject can be exercised.
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int left)
                {
                }

                public Animal(int right)
                {
                }
            }

            public class Dog : Animal
            {
                public int Value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true
            }));

        Assert.Equal(ErrorCodes.AmbiguousBaseConstructor, ex.ErrorCode);
        Assert.Contains("Multiple accessible constructors", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseOmitted_DerivedRecord_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog"
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name, bool active)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseFalse_DerivedRecord_HasNoBaseInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = false
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name, bool active)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_PrefixMatch_EmitsBaseAndDoesNotReassignPassedThrough()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var ctor = ExtractConstructor(updated, "Dog");
        Assert.Contains("public Dog(int age, string name, bool active) : base(age, name)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
        AssertGeneratedSourceCompiles(updated);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_AddNullChecks_GuardsForwardedReferenceParams()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public Animal(string name)
                {
                }
            }

            public record Dog : Animal
            {
                public string Name;
                public bool Active;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            AddNullChecks = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(string name, bool active) : base(name)", ctor);
        Assert.Contains("ArgumentNullException", ctor);
        Assert.Contains("nameof(name)", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
        var nameGuard = ctor.IndexOf("nameof(name)", StringComparison.Ordinal);
        var activeAssign = ctor.IndexOf("this.Active = active", StringComparison.Ordinal);
        Assert.True(nameGuard >= 0 && activeAssign > nameGuard, "Null check for forwarded name must appear before assignments.");
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_IncludeInheritedMembers_DoesNotReassignInherited()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithInheritedIntStringFieldsSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            IncludeInheritedMembers = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string label, bool flag) : base(age, label)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Label = label", ctor);
        Assert.Contains("this.Flag = flag", ctor);

        await using var withoutCallBase = await TempWorkspace.CreateAsync(DerivedRecordWithInheritedIntStringFieldsSource, "Dog.cs");
        var withoutResult = await new GenerateConstructorOperation(withoutCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withoutCallBase.SourcePath,
                TypeName = "Dog",
                IncludeInheritedMembers = true
            });
        Assert.True(withoutResult.Success);
        var withoutCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withoutCallBase.SourcePath)), "Dog");
        Assert.Contains("public Dog(bool flag, int age, string label)", withoutCtor);
        Assert.DoesNotContain(": base(", withoutCtor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_RecordInheritingClass_PrefixMatch_EmitsBase()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int age, string name)
                {
                }
            }

            public record Dog : Animal
            {
                public int Age;
                public string Name;
                public bool Active;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name, bool active) : base(age, name)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_OnlyAccessibleParameterlessBaseCtor_EmitsBase()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public string Species { get; set; }

                public Animal()
                {
                }
            }

            public record Dog : Animal
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(string name) : base()", ctor);
        Assert.Contains("this.Name = name", ctor);
        Assert.DoesNotContain("Species", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_CopyConstructorTrue_RejectsAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true,
                CopyConstructor = true
            }));

        Assert.Equal(ErrorCodes.CallBaseConflictsWithCopyConstructor, ex.ErrorCode);
        Assert.Contains("copyConstructor", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_RecordInheritingObject_HasNoBaseInitializer()
    {
        const string source = """
            namespace TestApp;

            public record Widget
            {
                public string Id;
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Widget");
        Assert.Contains("public Widget(string id, string name)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.Id = id", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_NoMatchingBaseCtorAndNoParameterless_RejectsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public Animal(int age)
                {
                }
            }

            public record Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true
            }));

        Assert.Equal(ErrorCodes.NoMatchingBaseConstructor, ex.ErrorCode);
        Assert.Contains("No accessible constructor", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_TwoEquallyMatchingBaseCtors_RejectsAmbiguityAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public Animal(int left)
                {
                }

                public Animal(int right)
                {
                }
            }

            public record Dog : Animal
            {
                public int Value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true
            }));

        Assert.Equal(ErrorCodes.AmbiguousBaseConstructor, ex.ErrorCode);
        Assert.Contains("Multiple accessible constructors", ex.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_PrivateOnlyBaseCtor_TreatsAsAbsent()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public Animal()
                {
                }

                private Animal(int age, string name)
                {
                }
            }

            public record Dog : Animal
            {
                public int Age;
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name) : base()", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_Preview_WritesNothing_AndDescribesInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedRecordWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("callBase : base(age, name) initializer", result.PendingChanges[0].Description);
        Assert.Contains("Animal(int, string)", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Dog(int age, string name, bool active) : base(age, name)", snippet);
        Assert.DoesNotContain("this.Age = age", snippet);
        Assert.Contains("this.Active = active", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_DerivedRecord_NoMatchingBaseCtor_Preview_RejectsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public record Animal
            {
                public Animal(int age)
                {
                }
            }

            public record Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true,
                Preview = true
            }));

        Assert.Equal(ErrorCodes.NoMatchingBaseConstructor, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_Struct_IsNoOp()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point");
        Assert.Contains("public Point(int x, int y)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.X = x", ctor);
        Assert.Contains("this.Y = y", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_Struct_IncludeInheritedMembers_DoesNotReorder()
    {
        const string source = """
            namespace TestApp;

            public struct Point
            {
                public int X;
                public string Label { get; set; }
            }
            """;

        await using var withoutCallBase = await TempWorkspace.CreateAsync(source, "Point.cs");
        var withoutResult = await new GenerateConstructorOperation(withoutCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withoutCallBase.SourcePath,
                TypeName = "Point",
                IncludeInheritedMembers = true
            });
        Assert.True(withoutResult.Success);
        var withoutCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withoutCallBase.SourcePath)), "Point");

        await using var withCallBase = await TempWorkspace.CreateAsync(source, "Point.cs");
        var withResult = await new GenerateConstructorOperation(withCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withCallBase.SourcePath,
                TypeName = "Point",
                IncludeInheritedMembers = true,
                CallBase = true
            });
        Assert.True(withResult.Success);
        var withCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withCallBase.SourcePath)), "Point");

        Assert.Contains("public Point(int x, string label)", withoutCtor);
        Assert.Contains("public Point(int x, string label)", withCtor);
        Assert.DoesNotContain(": base(", withCtor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_RecordStruct_IsNoOp()
    {
        await using var workspace = await TempWorkspace.CreateAsync(PointRecordStructSource, "Point.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Point",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Point");
        Assert.Contains("public Point(int x, int y)", ctor);
        Assert.DoesNotContain(": base(", ctor);
        Assert.Contains("this.X = x", ctor);
        Assert.Contains("this.Y = y", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_RecordStruct_IncludeInheritedMembers_DoesNotReorder()
    {
        const string source = """
            namespace TestApp;

            public record struct Point
            {
                public int X;
                public string Label { get; set; }
            }
            """;

        await using var withoutCallBase = await TempWorkspace.CreateAsync(source, "Point.cs");
        var withoutResult = await new GenerateConstructorOperation(withoutCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withoutCallBase.SourcePath,
                TypeName = "Point",
                IncludeInheritedMembers = true
            });
        Assert.True(withoutResult.Success);
        var withoutCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withoutCallBase.SourcePath)), "Point");

        await using var withCallBase = await TempWorkspace.CreateAsync(source, "Point.cs");
        var withResult = await new GenerateConstructorOperation(withCallBase.Context).ExecuteAsync(
            new GenerateConstructorParams
            {
                SourceFile = withCallBase.SourcePath,
                TypeName = "Point",
                IncludeInheritedMembers = true,
                CallBase = true
            });
        Assert.True(withResult.Success);
        var withCtor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(withCallBase.SourcePath)), "Point");

        Assert.Contains("public Point(int x, string label)", withoutCtor);
        Assert.Contains("public Point(int x, string label)", withCtor);
        Assert.DoesNotContain(": base(", withCtor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_PrivateOnlyBaseCtor_TreatsAsAbsent()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal()
                {
                }

                private Animal(int age, string name)
                {
                }
            }

            public class Dog : Animal
            {
                public int Age;
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age, string name) : base()", ctor);
        Assert.Contains("this.Age = age", ctor);
        Assert.Contains("this.Name = name", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_Preview_WritesNothing_AndDescribesInitializer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DerivedClassWithBaseIntStringCtorSource, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("callBase : base(age, name) initializer", result.PendingChanges[0].Description);
        Assert.Contains("Animal(int, string)", result.PendingChanges[0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Dog(int age, string name, bool active) : base(age, name)", snippet);
        Assert.DoesNotContain("this.Age = age", snippet);
        Assert.Contains("this.Active = active", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_ClassInheritingObject_Preview_SaysNoneAdded()
    {
        await using var workspace = await TempWorkspace.CreateAsync(WidgetWithFieldAndPropertySource, "Widget.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Widget",
            CallBase = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Contains("no callBase initializer was added", result.PendingChanges![0].Description);
        var snippet = result.PendingChanges[0].AfterSnippet!;
        Assert.Contains("public Widget(string id, string name)", snippet);
        Assert.DoesNotContain(": base(", snippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_NoMatchingBaseCtor_Preview_RejectsAndWritesNothing()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int age)
                {
                }
            }

            public class Dog : Animal
            {
                public string Name;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new GenerateConstructorParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Dog",
                CallBase = true,
                Preview = true
            }));

        Assert.Equal(ErrorCodes.NoMatchingBaseConstructor, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_LongestPrefixWins()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int age)
                {
                }

                public Animal(int age, string name)
                {
                }
            }

            public class Dog : Animal
            {
                public int Age;
                public string Name;
                public bool Active;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains(": base(age, name)", ctor);
        Assert.DoesNotContain(": base(age)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
        Assert.DoesNotContain("this.Name = name", ctor);
        Assert.Contains("this.Active = active", ctor);
    }

    [SkippableFact]
    public async Task GenerateConstructor_CallBaseTrue_NameMatchPrefersMatchingConstructor()
    {
        const string source = """
            namespace TestApp;

            public class Animal
            {
                public Animal(int age)
                {
                }

                public Animal(int years)
                {
                }
            }

            public class Dog : Animal
            {
                public int Age;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Dog.cs");
        var operation = new GenerateConstructorOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new GenerateConstructorParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Dog",
            CallBase = true
        });

        Assert.True(result.Success);
        var ctor = ExtractConstructor(NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath)), "Dog");
        Assert.Contains("public Dog(int age) : base(age)", ctor);
        Assert.DoesNotContain("this.Age = age", ctor);
    }

    #endregion

    #region Helper Methods

    private static void ValidateTypeForConstructor(INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.IsStatic)
        {
            throw new RefactoringException(
                ErrorCodes.TypeIsStatic,
                "Cannot add constructor to static class.");
        }
    }

    private static void ValidateMembersForConstructor(List<ISymbol> members)
    {
        if (members.Count == 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                "No members found to initialize in constructor.");
        }
    }

    private static void ValidateRequestedMembers(List<string> requested, List<string> available)
    {
        var availableSet = new HashSet<string>(available);
        var notFound = requested.Where(n => !availableSet.Contains(n)).ToList();

        if (notFound.Count > 0)
        {
            throw new RefactoringException(
                ErrorCodes.MemberNotFound,
                $"Members not found: {string.Join(", ", notFound)}");
        }
    }

    private static void ValidateConstructorSignature(
        List<List<string>> existingSignatures,
        List<string> newSignature)
    {
        var exists = existingSignatures.Any(sig =>
            sig.Count == newSignature.Count &&
            sig.SequenceEqual(newSignature));

        if (exists)
        {
            throw new RefactoringException(
                ErrorCodes.ConstructorExists,
                "A constructor with the same signature already exists.");
        }
    }

    private static string GenerateNullCheck(string paramName, string typeName, bool addNullChecks)
    {
        if (!addNullChecks)
            return string.Empty;

        // Simplified check: only add for reference types
        var valueTypes = new HashSet<string> { "int", "long", "double", "float", "bool", "char", "decimal", "byte", "short" };
        if (valueTypes.Contains(typeName))
            return string.Empty;

        return $"if ({paramName} == null) throw new ArgumentNullException(nameof({paramName}));";
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Remove leading underscore
        if (name.StartsWith("_"))
        {
            name = name.Substring(1);
        }

        // Convert first letter to lowercase
        if (char.IsUpper(name[0]))
        {
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    private static INamedTypeSymbol CreateStaticClassSymbol()
    {
        var source = "public static class StaticClass { }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var semanticModel = compilation.GetSemanticModel(tree);
        var classDeclaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First();

        return semanticModel.GetDeclaredSymbol(classDeclaration)
            ?? throw new InvalidOperationException("Could not create static class symbol");
    }

    private static INamedTypeSymbol CreateNonStaticClassSymbol()
    {
        var source = "public class NonStaticClass { }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var semanticModel = compilation.GetSemanticModel(tree);
        var classDeclaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First();

        return semanticModel.GetDeclaredSymbol(classDeclaration)
            ?? throw new InvalidOperationException("Could not create non-static class symbol");
    }

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateConstructorMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeHasConstructor(TypeDeclarationSyntax type) =>
        type.Members.OfType<ConstructorDeclarationSyntax>().Any();

    private static string ExtractConstructorFromType(TypeDeclarationSyntax type)
    {
        var ctor = type.Members.OfType<ConstructorDeclarationSyntax>().Single();
        return NormalizeNewlines(ctor.ToFullString());
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

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static void AssertNoIndexerAssignment(string ctor)
    {
        Assert.DoesNotContain("this[", ctor);
        Assert.DoesNotContain("this[]", ctor);
        Assert.DoesNotContain("Item", ctor);
        Assert.DoesNotContain("item", ctor);
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

    private static void AssertGeneratedSourceCompiles(string source)
    {
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };
        var runtime = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
        if (File.Exists(runtime))
            references.Add(MetadataReference.CreateFromFile(runtime));

        var compilation = CSharpCompilation.Create(
            "ClassBaseCopyCompileTest",
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, "Generated constructor did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    private static string ExtractConstructor(string source, string typeName, string visibility = "public")
    {
        var marker = $"{visibility} {typeName}(";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Constructor for {typeName} not found in:\n{source}");

        var brace = source.IndexOf('{', start);
        Assert.True(brace >= 0, $"Constructor body for {typeName} not found in:\n{source}");

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Unbalanced constructor braces for {typeName}.");
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required WorkspaceContext Context { get; init; }

        public string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Person.cs") =>
            CreateAsync((fileName, source));

        public static async Task<TempWorkspace> CreateAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpGenerateConstructor_" + Guid.NewGuid().ToString("N"));
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

            string? sourcePath = null;
            foreach (var (fileName, source) in files)
            {
                var path = Path.Combine(directory, fileName);
                await File.WriteAllTextAsync(path, source);
                sourcePath ??= path;
            }

            sourcePath ??= Path.Combine(directory, "Person.cs");

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

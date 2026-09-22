using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Contracts.Enums;
using RoslynMcp.Contracts.Errors;
using RoslynMcp.Contracts.Models;
using RoslynMcp.Core.Refactoring;
using RoslynMcp.Core.Refactoring.Extract;
using RoslynMcp.Core.Resolution;
using RoslynMcp.Core.Workspace;
using Xunit;

namespace RoslynMcp.Core.Tests.Refactoring.Extract;

/// <summary>
/// Operation-level tests for <see cref="ExtractInterfaceOperation"/>, including optional
/// <c>line</c>, <c>column</c>, <c>separateFile</c>, <c>targetFile</c>, <c>addInterfaceToType</c>,
/// and <c>allFiles</c>.
/// </summary>
public class ExtractInterfaceOperationTests
{
    private const string CalculatorSource = """
        namespace TestApp;

        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Multiply(int a, int b) => a * b;
        }
        """;

    private const string MixedIndexerSource = """
        namespace TestApp;

        public class Lookup
        {
            public int Count { get; set; }

            public string this[int i]
            {
                get => "";
                set { }
            }

            public int Add(int a, int b) => a + b;
        }
        """;

    #region P0 optional line disambiguation

    private const string NestedSameNamePersonSource = """
        namespace TestApp;

        public /* outer-person */ class Person
        {
            public string Name { get; set; }

            public /* nested-person */ class Person
            {
                public int Age { get; set; }
            }
        }
        """;

    private const string EnumFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* person-enum */ enum Person
            {
                Ready
            }
        }

        namespace TestApp
        {
            public /* person-class */ class Person
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string DelegateFirstThenSameNamedClassSource = """
        namespace Other
        {
            public /* person-delegate */ delegate void Person();
        }

        namespace TestApp
        {
            public /* person-class */ class Person
            {
                public string Name { get; set; }
            }
        }
        """;

    private const string LaterSameNamedPersonSource = """
        namespace Other
        {
            public class Person
            {
                public string Title { get; set; }
            }
        }

        namespace TestApp
        {
            public /* later-person */ class Person
            {
                public string Name { get; set; }
            }
        }
        """;

    [Fact]
    public void Line_DefaultsToNull()
    {
        var @params = new ExtractInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person",
            InterfaceName = "IPerson"
        };

        Assert.Null(@params.Line);
    }

    [Fact]
    public void Validate_InvalidLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = 0
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = -1
            }));

        Assert.Equal(ErrorCodes.InvalidLineNumber, ex.ErrorCode);
        Assert.Equal("1006", ex.ErrorCode);
    }

    [Fact]
    public void Column_DefaultsToNull()
    {
        var @params = new ExtractInterfaceParams
        {
            SourceFile = AbsoluteTestPath(),
            TypeName = "Person",
            InterfaceName = "IPerson"
        };

        Assert.Null(@params.Column);
    }

    [Fact]
    public void Validate_InvalidColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                InterfaceName = "IPerson",
                Column = 0
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Fact]
    public void Validate_NegativeColumn_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = "Person",
                InterfaceName = "IPerson",
                Column = -1
            }));

        Assert.Equal(ErrorCodes.InvalidColumnNumber, ex.ErrorCode);
        Assert.Equal("1007", ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTypeName_WithColumnAndLine_ThrowsMissingRequiredParam(string typeName)
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                SourceFile = AbsoluteTestPath(),
                TypeName = typeName,
                InterfaceName = "IPerson",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractInterface_OmittedLine_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.False(TypeImplementsInterface(types[1], "IPerson"));
        var iface = FindType(updated, "IPerson");
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
        Assert.Null(FindProperty(updated, "IPerson", "Age"));
        Assert.DoesNotContain(iface.Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text == "Age");
    }

    [SkippableFact]
    public async Task ExtractInterface_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeImplementsInterface(types[0], "IPerson"));
        Assert.True(TypeImplementsInterface(types[1], "IPerson"));
        Assert.Null(FindProperty(updated, "IPerson", "Name"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Age"));
    }

    [SkippableFact]
    public async Task ExtractInterface_LineOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = FindLine(NestedSameNamePersonSource, "outer-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.False(TypeImplementsInterface(types[1], "IPerson"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
        Assert.Null(FindProperty(updated, "IPerson", "Age"));
    }

    [SkippableFact]
    public async Task ExtractInterface_LineOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = FindLine(EnumFirstThenSameNamedClassSource, "person-enum")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("interface IPerson", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractInterface_LineOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("interface IPerson", updated, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractInterface_LineOnLaterSameFileType_AddInterfaceToType_AttachesToSelectedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(LaterSameNamedPersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = FindLine(LaterSameNamedPersonSource, "later-person"),
            AddInterfaceToType = true
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeImplementsInterface(types[0], "IPerson"));
        Assert.True(TypeImplementsInterface(types[1], "IPerson"));
        Assert.Null(FindProperty(updated, "IPerson", "Title"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
        Assert.Contains("interface IPerson", updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_SequentialExtracts_ReusedWorkspace_AttachesToSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Alpha
            {
                public string Name { get; set; }
            }

            public class Beta
            {
                public string Title { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Types.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var first = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Alpha",
            InterfaceName = "IAlpha"
        });
        Assert.True(first.Success);

        var second = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Beta",
            InterfaceName = "IBeta"
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Alpha").Concat(GetTypes(updated, "Beta")).ToList();
        var alpha = types.Single(t => t.Identifier.Text == "Alpha");
        var beta = types.Single(t => t.Identifier.Text == "Beta");
        Assert.True(TypeImplementsInterface(alpha, "IAlpha"));
        Assert.False(TypeImplementsInterface(alpha, "IBeta"));
        Assert.True(TypeImplementsInterface(beta, "IBeta"));
        Assert.False(TypeImplementsInterface(beta, "IAlpha"));
        Assert.NotNull(FindProperty(updated, "IAlpha", "Name"));
        Assert.NotNull(FindProperty(updated, "IBeta", "Title"));
        Assert.Null(FindProperty(updated, "IAlpha", "Title"));
        Assert.Null(FindProperty(updated, "IBeta", "Name"));
    }

    [SkippableFact]
    public async Task ExtractInterface_Line_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = FindLine(NestedSameNamePersonSource, "nested-person"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Extract interface IPerson", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].AfterSnippet);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Add IPerson to base list of Person", StringComparison.Ordinal));
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "nested-person"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_LineOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", FindLine(NestedSameNamePersonSource, "outer-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }

                public class Person // nested-person
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", identifierLine);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_LineMiss_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: 1);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnEnumIdentifier_PicksEnum()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-enum"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnClassIdentifier_PicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", FindLine(EnumFirstThenSameNamedClassSource, "person-class"));

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_LineOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [Fact]
    public void SpanCoversLine_TreatsEndAsExclusive()
    {
        var span = new FileLinePositionSpan(
            "t.cs",
            new LinePosition(0, 0),
            new LinePosition(2, 0));

        Assert.True(SpanCoverage.SpanCoversLine(span, 1));
        Assert.True(SpanCoverage.SpanCoversLine(span, 2));
        Assert.False(SpanCoverage.SpanCoversLine(span, 3));
        Assert.False(SpanCoverage.SpanCoversLine(span, 0));
    }

    [SkippableFact]
    public async Task ExtractInterface_OmittedLine_EnumFirstThenSameNamedClass_ExtractsFromClass()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EnumFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Single(types);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.Contains("interface IPerson", updated);
        Assert.Contains("enum Person", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("interface IPerson", updated[..updated.IndexOf("enum Person", StringComparison.Ordinal)]);
    }

    #endregion

    #region P0 optional column disambiguation

    private const string SameLineNestedPersonSource = """
        namespace TestApp;

        public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
        """;

    [SkippableFact]
    public async Task ExtractInterface_OmittedColumn_KeepsTypeNameFirstOrDefaultPick()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.False(TypeImplementsInterface(types[1], "IPerson"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
        Assert.Null(FindProperty(updated, "IPerson", "Age"));
    }

    [SkippableFact]
    public async Task ExtractInterface_OmittedColumn_LineOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = FindLine(NestedSameNamePersonSource, "nested-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeImplementsInterface(types[0], "IPerson"));
        Assert.True(TypeImplementsInterface(types[1], "IPerson"));
        Assert.Null(FindProperty(updated, "IPerson", "Name"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Age"));
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnOnNestedIdentifier_PicksNestedType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.False(TypeImplementsInterface(types[0], "IPerson"));
        Assert.True(TypeImplementsInterface(types[1], "IPerson"));
        Assert.Null(FindProperty(updated, "IPerson", "Name"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Age"));
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnOnOuterIdentifier_PicksOuterType()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var column = ColumnOf(SameLineNestedPersonSource, "Person { public string Name");

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = line,
            Column = column
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.False(TypeImplementsInterface(types[1], "IPerson"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
        Assert.Null(FindProperty(updated, "IPerson", "Age"));
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_FirstOrDefaultPicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_OmittedColumn_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: null, column: null);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnNestedIdentifier_PicksNested()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public int Age"));

        Assert.NotNull(found);
        Assert.True(found.Parent is TypeDeclarationSyntax outer && outer.Identifier.Text == "Person");
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnOuterIdentifier_PicksOuter()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(SameLineNestedPersonSource, "Person { public string Name"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_KeepsFirstMatch()
    {
        var root = CSharpSyntaxTree.ParseText(SameLineNestedPersonSource).GetRoot();
        var nestedColumn = ColumnOf(SameLineNestedPersonSource, "Person { public int Age");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line: null, nestedColumn);

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_EnumFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(EnumFirstThenSameNamedClassSource).GetRoot();
        var enumColumn = ColumnOf(EnumFirstThenSameNamedClassSource, "Person");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line: null, enumColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnWithoutLine_DelegateFirstPicksClass()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var delegateColumn = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line: null, delegateColumn);

        Assert.NotNull(found);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnContinuationIdentifier_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }

                public class Person // nested-person
                {
                    public int Age { get; set; }
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var startLine = FindLine(source, "public class");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", identifierLine, ColumnOf(source, "Person // split-person"));

        Assert.NotNull(found);
        Assert.False(found.Parent is TypeDeclarationSyntax);
        Assert.IsType<ClassDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnOnContinuationLine_PicksType()
    {
        const string source = """
            namespace TestApp;

            public class
                Person // split-person
            {
                public string Name { get; set; }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var startLine = FindLine(source, "public class\n    Person");
        var identifierLine = FindLine(source, "split-person");
        Assert.NotEqual(startLine, identifierLine);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = identifierLine,
            Column = ColumnOf(source, "Person // split-person")
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Single(types);
        Assert.True(TypeImplementsInterface(types[0], "IPerson"));
        Assert.NotNull(FindProperty(updated, "IPerson", "Name"));
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnEnumIdentifier_PicksEnum()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var line = FindLine(source, "public enum Person");
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person", line, ColumnOf(source, "Person { Ready }"));

        Assert.NotNull(found);
        Assert.IsType<EnumDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnOnEnumIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        const string source = """
            namespace TestApp { public enum Person { Ready } public class Person { public string Name { get; set; } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = FindLine(source, "public enum Person"),
                Column = ColumnOf(source, "Person { Ready }")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("interface IPerson", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnOnDelegateIdentifier_PicksDelegate()
    {
        var root = CSharpSyntaxTree.ParseText(DelegateFirstThenSameNamedClassSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(
            root, "Person",
            FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
            ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()"));

        Assert.NotNull(found);
        Assert.IsType<DelegateDeclarationSyntax>(found);
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnOnDelegateIdentifier_SameNamedClass_ThrowsInvalidSymbolKind()
    {
        await using var workspace = await TempWorkspace.CreateAsync(DelegateFirstThenSameNamedClassSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = FindLine(DelegateFirstThenSameNamedClassSource, "person-delegate"),
                Column = ColumnOf(DelegateFirstThenSameNamedClassSource, "Person()")
            }));

        Assert.Equal(ErrorCodes.InvalidSymbolKind, ex.ErrorCode);
        Assert.Equal("2020", ex.ErrorCode);
        Assert.Contains("not a supported target", ex.Message);
        var updated = await File.ReadAllTextAsync(workspace.SourcePath);
        Assert.Equal(before, updated);
        Assert.DoesNotContain("interface IPerson", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void FindTypeDeclaration_ColumnAndLineMiss_DoesNotFallBackToFirst()
    {
        var root = CSharpSyntaxTree.ParseText(NestedSameNamePersonSource).GetRoot();
        var found = ExtractInterfaceOperation.FindTypeDeclaration(root, "Person", line: 1, column: 1);

        Assert.Null(found);
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnAndLineMiss_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Person",
                InterfaceName = "IPerson",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractInterface_ColumnAndLine_UnknownTypeName_ThrowsTypeNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(NestedSameNamePersonSource, "Person.cs");
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Missing",
                InterfaceName = "IPerson",
                Line = 1,
                Column = 1
            }));

        Assert.Equal(ErrorCodes.TypeNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    [SkippableFact]
    public async Task ExtractInterface_Column_Preview_WritesNothing_AndDescribesRewrite()
    {
        await using var workspace = await TempWorkspace.CreateAsync(SameLineNestedPersonSource, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var line = FindLine(SameLineNestedPersonSource, "public class Person { public string Name");

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IPerson",
            Line = line,
            Column = ColumnOf(SameLineNestedPersonSource, "Person { public int Age"),
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Contains("Extract interface IPerson", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].Description);
        Assert.DoesNotContain("Name", result.PendingChanges[0].Description);
        Assert.Contains("Age", result.PendingChanges[0].AfterSnippet);
        Assert.Contains(result.PendingChanges, c =>
            c.Description.Contains("Add IPerson to base list of Person", StringComparison.Ordinal));
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

        Assert.True(SpanCoverage.SpanCoversColumn(span, line, startCol));
        Assert.True(SpanCoverage.SpanCoversColumn(span, line, endCol - 1));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, endCol));
        Assert.False(SpanCoverage.SpanCoversColumn(span, line, startCol - 1));
    }

    [SkippableFact]
    public async Task ExtractInterface_SequentialColumn_ReusedWorkspace_AttachesToSecondSelectedType()
    {
        const string source = """
            namespace TestApp;

            public class Person { public string Name { get; set; } public class Person { public int Age { get; set; } } }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Person.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var line = FindLine(source, "public class Person { public string Name");

        var first = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "IOuterPerson",
            Line = line,
            Column = ColumnOf(source, "Person { public string Name")
        });
        Assert.True(first.Success);

        // Recompute from the rewritten file. A per-execution annotation
        // must not leave the first selected type as the only recover-able
        // node in a reused workspace.
        var afterFirst = await File.ReadAllTextAsync(workspace.SourcePath);
        var second = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Person",
            InterfaceName = "INestedPerson",
            Line = FindLine(afterFirst, "Person { public int Age"),
            Column = ColumnOf(afterFirst, "Person { public int Age")
        });
        Assert.True(second.Success);

        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var types = GetTypes(updated, "Person");
        Assert.Equal(2, types.Count);
        Assert.True(TypeImplementsInterface(types[0], "IOuterPerson"));
        Assert.False(TypeImplementsInterface(types[0], "INestedPerson"));
        Assert.True(TypeImplementsInterface(types[1], "INestedPerson"));
        Assert.False(TypeImplementsInterface(types[1], "IOuterPerson"));
        Assert.NotNull(FindProperty(updated, "IOuterPerson", "Name"));
        Assert.NotNull(FindProperty(updated, "INestedPerson", "Age"));
        Assert.Null(FindProperty(updated, "IOuterPerson", "Age"));
        Assert.Null(FindProperty(updated, "INestedPerson", "Name"));
    }

    #endregion

    [SkippableFact]
    public async Task ExtractInterface_Default_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        AssertImplementsInterface(updated, "Calculator", "ICalculator");
        Assert.False(File.Exists(sibling));
        Assert.DoesNotContain(sibling, result.Changes!.FilesCreated);
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileFalse_WritesInterfaceIntoSourceFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = false
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("interface ICalculator", updated);
        AssertImplementsInterface(updated, "Calculator", "ICalculator");
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_WritesSiblingFileAndRemovesInterfaceFromSource()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(sibling));
        Assert.Contains(sibling, result.Changes!.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var interfaceFile = NormalizeNewlines(await File.ReadAllTextAsync(sibling));

        Assert.DoesNotContain("interface ICalculator", source);
        AssertImplementsInterface(source, "Calculator", "ICalculator");
        Assert.Contains("interface ICalculator", interfaceFile);
        Assert.Contains("int Add(int a, int b);", interfaceFile);
        Assert.Contains("int Multiply(int a, int b);", interfaceFile);
    }

    [SkippableFact]
    public async Task ExtractInterface_TargetFileWinsOverSeparateFile()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        var explicitTarget = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "CustomInterface.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            TargetFile = explicitTarget
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(explicitTarget));
        Assert.False(File.Exists(sibling));
        Assert.Contains(explicitTarget, result.Changes!.FilesCreated);
        Assert.DoesNotContain(sibling, result.Changes.FilesCreated);

        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var custom = NormalizeNewlines(await File.ReadAllTextAsync(explicitTarget));

        Assert.DoesNotContain("interface ICalculator", source);
        Assert.Contains("interface ICalculator", custom);
        AssertImplementsInterface(source, "Calculator", "ICalculator");
    }

    [SkippableFact]
    public async Task ExtractInterface_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_Preview_DoesNotWriteFiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.NotEmpty(result.PendingChanges);
        Assert.Equal(ChangeKind.Create, result.PendingChanges[0].ChangeType);
        Assert.Equal(sibling, result.PendingChanges[0].File);
        Assert.Contains("ICalculator", result.PendingChanges[0].AfterSnippet);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_SeparateFileTrue_SiblingExists_ThrowsTargetFileExists()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ICalculator.cs"));
        await File.WriteAllTextAsync(sibling, """
            namespace TestApp;

            public interface IExisting
            {
            }
            """);
        var sourceBefore = await File.ReadAllTextAsync(workspace.SourcePath);
        var siblingBefore = await File.ReadAllTextAsync(sibling);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Calculator",
                InterfaceName = "ICalculator",
                SeparateFile = true
            }));

        Assert.Equal(ErrorCodes.TargetFileExists, ex.ErrorCode);
        Assert.Equal("3019", ex.ErrorCode);
        Assert.Equal(sourceBefore, await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Equal(siblingBefore, await File.ReadAllTextAsync(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_Default_PublicIndexer_EmitsLegalIndexerAndCompiles()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var iface = FindType(updated, "ILookup");
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Equal("i", Assert.Single(indexer.ParameterList.Parameters).Identifier.Text);
        Assert.DoesNotContain(iface.Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        Assert.NotNull(FindProperty(updated, "ILookup", "Count"));
        Assert.NotNull(FindMethod(updated, "ILookup", "Add"));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableTheory]
    [InlineData("this[]")]
    [InlineData("Item")]
    [InlineData("this[int i]")]
    public async Task ExtractInterface_MembersFilter_IndexerAliases_ExtractsOnlyIndexer(string memberName)
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Members = new[] { memberName }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var iface = FindType(updated, "ILookup");
        Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains("this[int i]", updated);
        Assert.Null(FindProperty(updated, "ILookup", "Count"));
        Assert.Null(FindMethod(updated, "ILookup", "Add"));
        Assert.DoesNotContain(iface.Members.OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.Text.Contains("this", StringComparison.Ordinal));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_MembersFilter_OrdinaryProperty_DoesNotExtractIndexer()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup",
            Members = new[] { "Count" }
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var property = FindProperty(updated, "ILookup", "Count");
        Assert.NotNull(property);
        Assert.Empty(FindIndexers(updated, "ILookup"));
        Assert.DoesNotContain("this[]", property!.Identifier.Text);
        Assert.Null(FindMethod(updated, "ILookup", "Add"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_GetOnlyIndexer_EmitsGetOnly()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int Count { get; set; }

                public int this[int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.NotNull(FindProperty(updated, "ILookup", "Count"));
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_RefIndexer_KeepsRef()
    {
        const string source = """
            namespace TestApp;

            public class Cell
            {
                private int _value;

                public ref int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Cell.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Cell",
            InterfaceName = "ICell"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ICell"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.False(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref int this[int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_RefReadonlyIndexer_KeepsRefReadonly()
    {
        const string source = """
            namespace TestApp;

            public class Origin
            {
                private readonly int _value;

                public ref readonly int this[int i] => ref _value;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Origin.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Origin",
            InterfaceName = "IOrigin"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "IOrigin"));
        Assert.IsType<RefTypeSyntax>(indexer.Type);
        Assert.True(((RefTypeSyntax)indexer.Type).ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword));
        Assert.Contains("ref readonly int this[int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_Indexer_RefKindParameter_Preserved()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[in int i] => i;
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        var parameter = Assert.Single(indexer.ParameterList.Parameters);
        Assert.Contains(parameter.Modifiers, t => t.IsKind(SyntaxKind.InKeyword));
        Assert.Contains("this[in int i]", updated);
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_PublicIndexer_PrivateSetter_EmitsGetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[int i] { get => i; private set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_PublicIndexer_PrivateGetter_EmitsSetOnlyAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                public int this[int i] { private get => 0; set { } }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.DoesNotContain(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_InitOnlyIndexer_EmitsInitAndCompiles()
    {
        const string source = """
            namespace TestApp;

            public class Lookup
            {
                private int _value;

                public int this[int i]
                {
                    get => _value;
                    init => _value = value;
                }
            }
            """;

        await using var workspace = await TempWorkspace.CreateAsync(source, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Lookup",
            InterfaceName = "ILookup"
        });

        Assert.True(result.Success);
        var updated = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        var indexer = Assert.Single(FindIndexers(updated, "ILookup"));
        Assert.Contains(indexer.AccessorList!.Accessors, a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        Assert.Contains(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.InitAccessorDeclaration));
        Assert.DoesNotContain(indexer.AccessorList.Accessors, a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        Assert.Contains("this[int i]", updated);
        Assert.Contains("init;", ExtractMemberText(indexer));
        AssertImplementsInterface(updated, "Lookup", "ILookup");
        AssertCompiles(updated);
    }

    [SkippableFact]
    public async Task ExtractInterface_Indexer_Preview_DescribesIndexerAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);
        var sibling = Path.GetFullPath(Path.Combine(workspace.DirectoryPath, "ILookup.cs"));

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
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
        Assert.False(File.Exists(sibling));
    }

    [SkippableFact]
    public async Task ExtractInterface_MembersFilter_UnknownIndexerAlias_ThrowsMemberNotFound()
    {
        await using var workspace = await TempWorkspace.CreateAsync(MixedIndexerSource, "Lookup.cs");
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var before = await File.ReadAllTextAsync(workspace.SourcePath);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                SourceFile = workspace.SourcePath,
                TypeName = "Lookup",
                InterfaceName = "ILookup",
                Members = new[] { "DoesNotExist" }
            }));

        Assert.Equal(ErrorCodes.MemberNotFound, ex.ErrorCode);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.SourcePath));
    }

    #region allFiles

    private const string EligibleFileA = """
        namespace TestApp;

        public class FileA
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    private const string EligibleFileB = """
        namespace TestApp;

        public class FileB
        {
            public string Name { get; set; }
        }
        """;

    private const string IneligibleStaticFile = """
        namespace TestApp;

        public static class FileC
        {
            public static int Add(int a, int b) => a + b;
        }
        """;

    [Fact]
    public void Validate_AllFilesTrue_WithoutSourceFileOrTypeName_DoesNotThrow()
    {
        ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
        {
            AllFiles = true
        });
    }

    [Fact]
    public void Validate_AllFilesTrue_WithTypeName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("allFiles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithInterfaceName_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                AllFiles = true,
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_WithLine_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                AllFiles = true,
                Line = 1
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesTrue_RelativeSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                AllFiles = true,
                SourceFile = "relative.cs"
            }));

        Assert.Equal(ErrorCodes.InvalidSourcePath, ex.ErrorCode);
    }

    [Fact]
    public void Validate_AllFilesFalse_WithoutSourceFile_Throws()
    {
        var ex = Assert.Throws<RefactoringException>(() =>
            ExtractInterfaceOperation.Validate(new ExtractInterfaceParams
            {
                AllFiles = false,
                TypeName = "FileA",
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
        Assert.Contains("sourceFile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildAllFilesDescription_SingularAndPlural()
    {
        Assert.Equal("Extract interface", ExtractInterfaceOperation.BuildAllFilesDescription(1));
        Assert.Equal("Extract 2 interfaces", ExtractInterfaceOperation.BuildAllFilesDescription(2));
    }

    [Fact]
    public void DeriveInterfaceName_PrefixesI()
    {
        Assert.Equal("IFileA", ExtractInterfaceOperation.DeriveInterfaceName("FileA"));
    }

    [SkippableFact]
    public async Task ExtractInterface_OmittedAllFiles_KeepsSingleSiteExtract()
    {
        await using var workspace = await TempWorkspace.CreateAsync(CalculatorSource);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            SourceFile = workspace.SourcePath,
            TypeName = "Calculator",
            InterfaceName = "ICalculator",
            SeparateFile = true
        });

        Assert.True(result.Success);
        var sibling = Path.Combine(workspace.DirectoryPath, "ICalculator.cs");
        Assert.True(File.Exists(sibling));
        var source = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePath));
        Assert.Contains("ICalculator", source, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_ExtractsEligibleTypesAcrossFiles()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleStaticFile));
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var beforeC = await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.False(result.Preview);
        var updatedA = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        var updatedB = NormalizeNewlines(await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        AssertImplementsInterface(updatedA, "FileA", "IFileA");
        AssertImplementsInterface(updatedB, "FileB", "IFileB");
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileA.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileB.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileC.cs")));
        Assert.Equal(beforeC, await File.ReadAllTextAsync(workspace.SourcePaths["FileC.cs"]));
        Assert.True(result.Changes!.FilesCreated.Count >= 2);
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.Contains(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileC.cs"]));
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_WithoutSourceFileOrTypeName_Succeeds()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.True(result.Changes!.FilesCreated.Count >= 2);
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesFalse_WithoutSourceFile_MissingRequiredParam()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                AllFiles = false,
                TypeName = "FileA",
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_WithTypeName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                AllFiles = true,
                TypeName = "FileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_WithInterfaceName_Rejects()
    {
        await using var workspace = await TempWorkspace.CreateAsync(EligibleFileA);
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var ex = await Assert.ThrowsAsync<RefactoringException>(() =>
            operation.ExecuteAsync(new ExtractInterfaceParams
            {
                AllFiles = true,
                InterfaceName = "IFileA"
            }));

        Assert.Equal(ErrorCodes.MissingRequiredParam, ex.ErrorCode);
    }

    [SkippableFact]
    public async Task ExtractInterface_PreviewAllFiles_AggregatesChangedFilesAndWritesNothing()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB),
            ("FileC.cs", IneligibleStaticFile));
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var beforeA = await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            AllFiles = true,
            Preview = true
        });

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.NotNull(result.PendingChanges);
        Assert.True(result.PendingChanges!.Count >= 2);
        Assert.Equal(beforeA, await File.ReadAllTextAsync(workspace.SourcePaths["FileA.cs"]));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileA.cs")));
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_EveryFileIneligible_SucceedsWithEmptyChanges()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileC.cs", IneligibleStaticFile));
        var operation = new ExtractInterfaceOperation(workspace.Context);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            AllFiles = true
        });

        Assert.True(result.Success);
        Assert.Empty(result.Changes!.FilesModified);
        Assert.Empty(result.Changes.FilesCreated);
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileC.cs")));
    }

    [SkippableFact]
    public async Task ExtractInterface_AllFilesTrue_OptionalSourceFile_LimitsWalk()
    {
        await using var workspace = await TempWorkspace.CreateWithFilesAsync(
            ("FileA.cs", EligibleFileA),
            ("FileB.cs", EligibleFileB));
        var operation = new ExtractInterfaceOperation(workspace.Context);
        var beforeB = await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]);

        var result = await operation.ExecuteAsync(new ExtractInterfaceParams
        {
            AllFiles = true,
            SourceFile = workspace.SourcePaths["FileA.cs"]
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileA.cs")));
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "IFileB.cs")));
        Assert.Equal(beforeB, await File.ReadAllTextAsync(workspace.SourcePaths["FileB.cs"]));
        Assert.Contains(result.Changes!.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileA.cs"]));
        Assert.DoesNotContain(result.Changes.FilesModified, p => PathEquals(p, workspace.SourcePaths["FileB.cs"]));
    }

    #endregion

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n");

    private static string AbsoluteTestPath() =>
        Path.Combine(Path.GetTempPath(), "RoslynMcpExtractInterfaceMissing.cs");

    private static IReadOnlyList<TypeDeclarationSyntax> GetTypes(string source, string name) =>
        CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.Text == name)
            .ToList();

    private static bool TypeImplementsInterface(TypeDeclarationSyntax type, string interfaceName) =>
        type.BaseList?.Types.Any(t => t.Type.ToString() == interfaceName) == true;

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

    // Single-line snippets only — IndexOf of an LF-only snippet missed
    // CRLF checkouts (FindMethod_ColumnOnContinuationLine on #200 / #214).
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

    private static string ExtractMemberText(MemberDeclarationSyntax member) =>
        NormalizeNewlines(member.NormalizeWhitespace().ToFullString());

    private static void AssertCompiles(string source)
    {
        var compilation = CSharpCompilation.Create(
                "ExtractInterfaceCompileTest",
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
        Assert.True(errors.Count == 0, "Generated extract_interface members did not compile:\n" + string.Join("\n", errors) + "\n\n" + source);
    }

    /// <summary>
    /// Base-list trivia from <c>WithBaseList</c> may omit spaces; compare a compacted form.
    /// </summary>
    private static void AssertImplementsInterface(string source, string typeName, string interfaceName)
    {
        var compact = new string(source.Where(c => !char.IsWhiteSpace(c)).ToArray());
        Assert.Contains($"class{typeName}:{interfaceName}", compact);
    }

    private sealed class TempWorkspace : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required string ProjectPath { get; init; }
        public required string SourcePath { get; init; }
        public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
        public required WorkspaceContext Context { get; init; }

        public static Task<TempWorkspace> CreateAsync(string source, string fileName = "Calculator.cs") =>
            CreateWithFilesAsync((fileName, source));

        public static async Task<TempWorkspace> CreateWithFilesAsync(params (string FileName, string Source)[] files)
        {
            Skip.IfNot(ModuleInitializer.MsBuildAvailable, ModuleInitializer.MsBuildError ?? "MSBuild not available");

            var directory = Path.Combine(Path.GetTempPath(), "RoslynMcpExtractInterface_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var projectPath = Path.Combine(directory, "TestApp.csproj");
            var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>
                  </PropertyGroup>
                </Project>
                """);

            string? firstSource = null;
            foreach (var (fileName, source) in files)
            {
                var sourcePath = Path.Combine(directory, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                await File.WriteAllTextAsync(sourcePath, source);
                sourcePaths[fileName] = sourcePath;
                firstSource ??= sourcePath;
            }

            try
            {
                var provider = new MSBuildWorkspaceProvider();
                var context = await provider.CreateContextAsync(projectPath);
                foreach (var sourcePath in sourcePaths.Values)
                {
                    if (context.GetDocumentByPath(sourcePath) == null)
                    {
                        context.Dispose();
                        throw new InvalidOperationException($"Workspace loaded but did not include {sourcePath}.");
                    }
                }

                return new TempWorkspace
                {
                    DirectoryPath = directory,
                    ProjectPath = projectPath,
                    SourcePath = firstSource!,
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
}
